using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Azure.Core;
using Azure.Identity;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.UserData;

/// <summary>
/// Owns the interactive developer identity used by an explicit Azure production-data sync.
/// It deliberately does not share Azure CLI's token cache: Conditional Access can invalidate
/// that cache independently, and the sync must not require the CLI to be installed.
/// </summary>
internal sealed record AzureUserContext(
    TokenCredential Credential,
    string PrincipalId,
    string DisplayName,
    bool CanActivateEligibleRoles)
{
    internal const string ArmScope = "https://management.azure.com/.default";

    // Azure CLI's public client registration supports device-code authentication for ARM. We use
    // the registration only as a public OAuth client; neither the CLI executable nor its cache is
    // involved.
    private const string DefaultPublicClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    internal static async Task<AzureUserContext> CreateAsync(
        UserDataSyncOptions options,
        ILogger logger,
        IInteractionService? interactions,
        CancellationToken ct)
    {
        var tenantId = options.AzureTenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException(
                "AzureTenantId is required for an Azure production snapshot. Configure it with AuthenticateAzureUser(...).");

        var requestedIdentity = NormalizeUsername(options.AzureUsername);
        if (Guid.TryParse(requestedIdentity, out var applicationClientId))
        {
            return await CreateApplicationAsync(
                    tenantId, applicationClientId.ToString(), logger, interactions, ct)
                .ConfigureAwait(false);
        }

        var requestedUsername = requestedIdentity;
        var clientId = string.IsNullOrWhiteSpace(options.AzureAuthenticationClientId)
            ? DefaultPublicClientId
            : options.AzureAuthenticationClientId;
        var cacheDirectory = GetCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);
        using var browserCapture = PrepareBrowserLaunchCapture(cacheDirectory, logger);

        var record = await ReadRecordAsync(cacheDirectory, tenantId, clientId, requestedUsername, ct)
            .ConfigureAwait(false);
        if (record is not null && requestedUsername is not null &&
            !string.Equals(record.Username, requestedUsername, StringComparison.OrdinalIgnoreCase))
        {
            record = null;
        }

        var credentialOptions = new InteractiveBrowserCredentialOptions
        {
            TenantId = tenantId,
            ClientId = clientId,
            AuthenticationRecord = record,
            LoginHint = requestedUsername,
            BrowserCustomization = new BrowserCustomizationOptions
            {
                SuccessMessage = "Azure-login er gennemført. Du kan lukke dette vindue og vende tilbage til Aspire.",
                ErrorMessage = "Azure-login kunne ikke gennemføres. Vend tilbage til Aspire-dashboardet for detaljer.",
            },
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = $"agentics-aspire-user-data-{ShortHash($"{tenantId}:{clientId}")}",
                // Devcontainers commonly have no desktop keyring. The cache remains in the
                // current OS user's private application-data directory, just like other local
                // developer credential caches.
                UnsafeAllowUnencryptedStorage = true,
            },
        };

        var credential = new InteractiveBrowserCredential(credentialOptions);
        try
        {
            if (record is null)
            {
                var account = requestedUsername ?? "din Azure-konto";
                logger.LogInformation("No cached Azure sync session for {Username}; opening browser sign-in", account);
                await WaitForDashboardAsync(interactions, ct).ConfigureAwait(false);
                using var authenticationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var authentication = credential.AuthenticateAsync(
                    new TokenRequestContext([ArmScope]), authenticationCts.Token);

                if (browserCapture is not null)
                {
                    var authorizationUrl = await browserCapture.WaitForUrlAsync(authentication, ct).ConfigureAwait(false);
                    if (authorizationUrl is not null)
                    {
                        Notify(
                            interactions,
                            "Azure-login kræves",
                            $"Produktions-syncen venter på browser-login som **{account}**. " +
                            "Linket vender tilbage til den lokale callback-listener og fortsætter synkroniseringen automatisk.",
                            MessageIntent.Information,
                            "Åbn Microsoft-login",
                            authorizationUrl);
                    }
                    else if (!authentication.IsCompleted)
                    {
                        authenticationCts.Cancel();
                        throw new InvalidOperationException(
                            "Azure browser authentication started, but its authorization URL could not be captured for the Aspire dashboard.");
                    }
                }
                else
                {
                    Notify(
                        interactions,
                        "Azure-login kræves",
                        $"Produktions-syncen venter på browser-login som **{account}**. " +
                        "Gennemfør Microsoft-loginvinduet; synkroniseringen fortsætter automatisk bagefter.",
                        MessageIntent.Information);
                }

                record = await authentication.ConfigureAwait(false);

                if (requestedUsername is not null &&
                    !string.Equals(record.Username, requestedUsername, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Signed in as '{record.Username}', but the production sync requested '{requestedUsername}'. " +
                        "Run the command again and use the requested account in the device-code window.");
                }

                await WriteRecordAsync(cacheDirectory, tenantId, clientId, record, ct).ConfigureAwait(false);
            }

            var token = await credential.GetTokenAsync(
                new TokenRequestContext([ArmScope]), ct).ConfigureAwait(false);
            var principalId = ReadJwtClaim(token.Token, "oid");
            if (!Guid.TryParse(principalId, out _))
                throw new InvalidOperationException("The Azure ARM access token did not contain a valid user object id.");

            var username = NormalizeUsername(record.Username) ??
                ReadJwtClaim(token.Token, "preferred_username") ??
                requestedUsername ?? "unknown";

            if (requestedUsername is not null &&
                !string.Equals(username, requestedUsername, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The cached Azure session belongs to '{username}', not '{requestedUsername}'.");
            }

            logger.LogInformation("Using Azure production-sync identity {Username}", username);
            Notify(
                interactions,
                "Azure-login gennemført",
                $"Produktions-syncen fortsætter som **{username}**.",
                MessageIntent.Success);
            return new AzureUserContext(credential, principalId!, username, CanActivateEligibleRoles: true);
        }
        catch (AuthenticationFailedException ex)
        {
            Notify(
                interactions,
                "Azure-login fejlede",
                "Microsoft Entra ID afviste loginflowet. Åbn sync-resource-loggen for den tekniske fejl.",
                MessageIntent.Error);
            throw new InvalidOperationException(
                "Azure user authentication failed. Re-run the sync and complete the browser sign-in. " +
                ex.Message,
                ex);
        }
    }

    private static async Task<AzureUserContext> CreateApplicationAsync(
        string tenantId,
        string clientId,
        ILogger logger,
        IInteractionService? interactions,
        CancellationToken ct)
    {
        await WaitForDashboardAsync(interactions, ct).ConfigureAwait(false);
        if (interactions is null || !interactions.IsAvailable)
        {
            throw new InvalidOperationException(
                $"Client secret input is required for Azure application {clientId}, but the Aspire dashboard is not connected.");
        }

        var result = await interactions.PromptInputAsync(
                "Azure application login",
                $"Enter the client secret **value** for application `{clientId}`. " +
                "The value is used only for this production sync and is not saved.",
                new InteractionInput
                {
                    Name = "client-secret",
                    Label = "Client secret",
                    Description = "Use the secret value from Entra ID, not its secret ID.",
                    InputType = InputType.SecretText,
                    Required = true,
                    MaxLength = 4096,
                },
                cancellationToken: ct)
            .ConfigureAwait(false);

        var clientSecret = result.Canceled ? null : result.Data?.Value;
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new OperationCanceledException("Azure application login was canceled or no client secret was supplied.", ct);

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct)
                .ConfigureAwait(false);
            var principalId = ReadJwtClaim(token.Token, "oid");
            if (!Guid.TryParse(principalId, out _))
                throw new InvalidOperationException("The Azure application token did not contain a valid service principal object id.");

            logger.LogInformation("Using Azure production-sync application {ClientId}", clientId);
            Notify(
                interactions,
                "Azure application authenticated",
                $"Production sync continues as application **{clientId}**.",
                MessageIntent.Success);
            return new AzureUserContext(
                credential,
                principalId!,
                clientId,
                CanActivateEligibleRoles: false);
        }
        catch (AuthenticationFailedException ex)
        {
            Notify(
                interactions,
                "Azure application login failed",
                "Entra ID rejected the client id or client secret. Open the sync resource log for details.",
                MessageIntent.Error);
            throw new InvalidOperationException(
                $"Azure application authentication failed for client {clientId}. Verify the tenant, client id and secret value. " +
                ex.Message,
                ex);
        }
    }

    private static void Notify(
        IInteractionService? interactions,
        string title,
        string message,
        MessageIntent intent,
        string? linkText = null,
        string? linkUrl = null)
    {
        if (interactions is null || !interactions.IsAvailable) return;

        // A dashboard notification completes only when it is dismissed. Authentication must not
        // wait for that, so the interaction deliberately runs independently of the sync task.
        _ = Task.Run(async () =>
        {
            try
            {
                await interactions.PromptNotificationAsync(
                    title,
                    message,
                    new NotificationInteractionOptions
                    {
                        Intent = intent,
                        EnableMessageMarkdown = true,
                        LinkText = linkText,
                        LinkUrl = linkUrl,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The dashboard may disconnect while the browser authentication is in progress.
                // The resource log remains the fallback diagnostic channel.
            }
        });
    }

    private static async Task WaitForDashboardAsync(IInteractionService? interactions, CancellationToken ct)
    {
        if (interactions is null) return;

        // The sync resource starts in parallel with the dashboard. Give the interaction endpoint
        // a brief chance to become available so the sign-in notice is visible before the browser
        // opens, without making authentication depend permanently on dashboard availability.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!interactions.IsAvailable && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(200, ct).ConfigureAwait(false);
    }

    private static async Task<AuthenticationRecord?> ReadRecordAsync(
        string directory,
        string tenantId,
        string clientId,
        string? requestedUsername,
        CancellationToken ct)
    {
        var paths = requestedUsername is null
            ? new[] { RecordPath(directory, tenantId, clientId, "last") }
            : new[]
            {
                RecordPath(directory, tenantId, clientId, requestedUsername),
                RecordPath(directory, tenantId, clientId, "last"),
            };

        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(path)) continue;
            try
            {
                await using var stream = File.OpenRead(path);
                var record = await AuthenticationRecord.DeserializeAsync(stream, ct).ConfigureAwait(false);
                if (requestedUsername is null ||
                    string.Equals(record.Username, requestedUsername, StringComparison.OrdinalIgnoreCase))
                    return record;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A partial or old record should trigger a fresh login, not make the support
                // workflow permanently unusable.
            }
        }

        return null;
    }

    private static async Task WriteRecordAsync(
        string directory,
        string tenantId,
        string clientId,
        AuthenticationRecord record,
        CancellationToken ct)
    {
        var username = NormalizeUsername(record.Username) ?? "last";
        foreach (var key in new[] { username, "last" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = RecordPath(directory, tenantId, clientId, key);
            var temporary = path + $".new-{Guid.NewGuid():N}";
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await record.SerializeAsync(stream, ct).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
    }

    private static string RecordPath(string directory, string tenantId, string clientId, string account) =>
        Path.Combine(directory, $"{ShortHash($"{tenantId}:{clientId}:{account.ToLowerInvariant()}")}.record");

    private static string GetCacheDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(local, "Agentics", "Aspire", "UserDataAuth");
    }

    private static BrowserLaunchCapture? PrepareBrowserLaunchCapture(string cacheDirectory, ILogger logger)
    {
        if (!OperatingSystem.IsLinux()) return null;

        var shimDirectory = Path.Combine(cacheDirectory, "browser-launcher");
        var existingLauncher = FindOnPath("xdg-open");
        if (existingLauncher is not null &&
            !Path.GetFullPath(existingLauncher).StartsWith(
                Path.GetFullPath(shimDirectory) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return null;
        }

        Directory.CreateDirectory(shimDirectory);
        foreach (var stale in Directory.EnumerateFiles(shimDirectory, "authorization-*.url"))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // A concurrent authentication owns it; the production sync lock prevents that
                // for this feature, but leaving an in-use short-lived URL is safer than failing.
            }
        }
        var shim = Path.Combine(shimDirectory, "xdg-open");
        var capturePath = Path.Combine(shimDirectory, $"authorization-{Guid.NewGuid():N}.url");
        var quotedCapturePath = capturePath.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        File.WriteAllText(
            shim,
            $"#!/bin/sh\numask 077\nprintf '%s' \"$1\" > '{quotedCapturePath}'\n");
        File.SetUnixFileMode(
            shim,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Contains(shimDirectory, StringComparer.Ordinal))
        {
            Environment.SetEnvironmentVariable("PATH", shimDirectory + Path.PathSeparator + currentPath);
        }

        logger.LogDebug("Azure browser authorization URL will be shown in the Aspire dashboard");
        return new BrowserLaunchCapture(capturePath);
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private sealed class BrowserLaunchCapture(string path) : IDisposable
    {
        internal async Task<string?> WaitForUrlAsync(Task authentication, CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (!authentication.IsCompleted && DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    var url = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();
                    if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                        parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        return parsed.AbsoluteUri;
                    }
                }
                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            return null;
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // The authorization URL expires quickly; a failed cleanup is harmless and the
                // next run uses a unique file.
            }
        }
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private static string? NormalizeUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? null : username.Trim();

    internal static string? ReadJwtClaim(string token, string claim)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return json.RootElement.TryGetProperty(claim, out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
