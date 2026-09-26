using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.UserData;

/// <summary>
/// The fetch itself: resolve the remote directory, stream it down, redact it, mark it.
/// </summary>
internal static class RemoteUserData
{
    private static readonly HttpClient ArmClient = new();
    // Increment when the meaning of a staged snapshot changes. Version 2 preserves useful
    // sign-in activity while still stripping identity-provider subject ids.
    private const int ManifestVersion = 2;

    /// <summary>
    /// Written into every fetched directory. It is not documentation — the application reads it
    /// and refuses to do anything that would reach real users (sending mail being the obvious
    /// one). Keep the name and the <c>source</c> value stable; apps match on them.
    /// </summary>
    internal const string MarkerFileName = ".snapshot-origin.json";

    internal static async Task FetchAsync(
        UserDataSyncOptions options,
        string localPath,
        ILogger logger,
        IInteractionService? interactions,
        CancellationToken ct)
    {
        // The OS lock is released even if the AppHost is killed. The small lock file itself may
        // remain, which is intentional: OpenOrCreate locks the inode rather than treating the
        // file's existence as proof that a sync is alive.
        using var syncLock = AcquireSyncLock(localPath);
        await RemoveStaleStagingAsync(localPath, logger).ConfigureAwait(false);

        // A unique staging directory means an orphaned downloader from an abruptly terminated
        // older AppHost can never race a new run by recreating entries while they are deleted.
        var staging = $"{localPath}.incoming-{Environment.ProcessId}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(staging);

        try
        {
            string source;
            string remotePath;

            if (options.AzureContainerAppName is not null)
            {
                var identity = await AzureUserContext.CreateAsync(options, logger, interactions, ct).ConfigureAwait(false);
                options.AzureUserAccessProxy?.UseCredential(identity.Credential, identity.DisplayName);
                var azure = await ResolveAzureFilesAsync(options, identity, logger, ct).ConfigureAwait(false);
                source = $"azure://{options.AzureSubscriptionId}/{options.AzureResourceGroup}/{options.AzureContainerAppName}";
                remotePath = $"{azure.AccountName}/{azure.ShareName}";
                logger.LogInformation("Indexing Azure Files share {Account}/{Share}", azure.AccountName, azure.ShareName);
                var share = CreateShareClient(azure, identity.Credential);
                var remoteFiles = await RunAzureFilesWithEligibleRoleActivationAsync(
                    options, identity, logger, token => ListAzureFilesAsync(options, share, token), ct).ConfigureAwait(false);
                var previous = await ReadCompatibleManifestAsync(
                    localPath, source, remotePath, options, logger, ct).ConfigureAwait(false);
                var sync = await BuildIncrementalAzureSnapshotAsync(
                    options, localPath, staging, remoteFiles, previous, logger, ct).ConfigureAwait(false);

                var redacted = Redact(staging, options.Redactions, logger);
                logger.LogInformation("Redacted {Count} file(s)", redacted);

                var manifest = await CreateManifestAsync(staging, remoteFiles, ct).ConfigureAwait(false);
                await WriteMarkerAsync(staging, source, remotePath, options, manifest, ct).ConfigureAwait(false);

                logger.LogInformation(
                    "Production snapshot ready: {Reused} reused, {Downloaded} downloaded ({Bytes:N0} bytes), " +
                    "{Removed} removed remotely",
                    sync.Reused, sync.Downloaded, sync.DownloadedBytes, sync.Removed);
            }
            else
            {
                var host = options.Host
                    ?? throw new InvalidOperationException(
                        "No production source configured — call FromCoolify, FromPath or FromAzureContainerApp.");
                remotePath = options.RemotePath ?? string.Empty;
                if (remotePath.Length == 0)
                {
                    logger.LogInformation("Resolving the data directory for {Fqdn} on {Host}", options.CoolifyFqdn, host);
                    remotePath = await ResolveCoolifyPathAsync(host, options, ct).ConfigureAwait(false);
                    logger.LogInformation("Found {RemotePath}", remotePath);
                }

                source = host;
                logger.LogInformation("Copying {Host}:{RemotePath}", host, remotePath);
                await StreamDirectoryAsync(host, remotePath, staging, options.Excludes, ct).ConfigureAwait(false);
            }

            if (options.AzureContainerAppName is null)
            {
                var redacted = Redact(staging, options.Redactions, logger);
                logger.LogInformation("Redacted {Count} file(s)", redacted);
                await WriteMarkerAsync(staging, source, remotePath, options, manifest: null, ct).ConfigureAwait(false);
            }

            Install(staging, localPath, options.RetainDays, logger);
        }
        finally
        {
            await DeleteStagingAsync(staging, logger).ConfigureAwait(false);
        }
    }

    private static async Task RemoveStaleStagingAsync(string localPath, ILogger logger)
    {
        var fullPath = Path.GetFullPath(localPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null || !Directory.Exists(parent)) return;

        var prefix = Path.GetFileName(fullPath) + ".incoming";
        foreach (var directory in Directory.EnumerateDirectories(parent, prefix + "*"))
        {
            await DeleteStagingAsync(directory, logger).ConfigureAwait(false);
        }
    }

    private static async Task DeleteStagingAsync(string directory, ILogger logger)
    {
        if (!Directory.Exists(directory)) return;

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                logger.LogInformation("Removed incomplete production snapshot {Staging}", Path.GetFileName(directory));
                return;
            }
            catch (IOException ex)
            {
                if (attempt == 10)
                {
                    logger.LogWarning(
                        "Could not remove incomplete production snapshot {Staging}: {Message}. " +
                        "The next sync uses a different staging directory",
                        Path.GetFileName(directory), ex.Message);
                    return;
                }

                // A killed CLI child may take a moment to close its final file handle.
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    private static FileStream AcquireSyncLock(string localPath)
    {
        var lockPath = localPath + ".sync.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);

        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Another production snapshot sync is already using {localPath}. Wait for it to finish before trying again.",
                ex);
        }
    }

    private sealed record AzureFilesSource(string AccountName, string ShareName);

    private sealed record RemoteFile(
        string Path, string ETag, long Size, string? LastModified, ShareFileClient Client);

    private sealed record ManifestFile(string Path, string ETag, long Size, string? LastModified, string Sha256);

    private sealed record SnapshotManifest(int Version, IReadOnlyDictionary<string, ManifestFile> Files);

    private sealed record SyncResult(int Reused, int Downloaded, long DownloadedBytes, int Removed);

    /// <summary>
    /// Resolves the Azure Files share through the Container App's mount. Keeping only the
    /// deployment coordinates in source means generated environment and storage names may change
    /// without breaking every developer's support command.
    /// </summary>
    private static async Task<AzureFilesSource> ResolveAzureFilesAsync(
        UserDataSyncOptions options, AzureUserContext identity, ILogger logger, CancellationToken ct)
    {
        var subscription = options.AzureSubscriptionId!;
        var resourceGroup = options.AzureResourceGroup!;
        var appName = options.AzureContainerAppName!;

        var appUrl = $"https://management.azure.com/subscriptions/{subscription}/resourceGroups/{resourceGroup}/" +
                     $"providers/Microsoft.App/containerApps/{appName}?api-version=2024-03-01";
        var app = await RunArmReadWithEligibleRoleActivationAsync(
            options, identity, logger, appUrl, ct).ConfigureAwait(false);

        using var appJson = JsonDocument.Parse(app);
        var properties = appJson.RootElement.GetProperty("properties");
        var environmentId = properties.GetProperty("managedEnvironmentId").GetString();
        if (string.IsNullOrWhiteSpace(environmentId))
            throw new InvalidOperationException($"Container App {resourceGroup}/{appName} has no managed environment.");

        string? volumeName = null;
        foreach (var container in properties.GetProperty("template").GetProperty("containers").EnumerateArray())
        {
            if (!container.TryGetProperty("volumeMounts", out var mounts)) continue;
            foreach (var mount in mounts.EnumerateArray())
            {
                if (mount.TryGetProperty("mountPath", out var path) &&
                    string.Equals(path.GetString()?.TrimEnd('/'), options.ContainerDataPath.TrimEnd('/'),
                        StringComparison.Ordinal))
                {
                    volumeName = mount.GetProperty("volumeName").GetString();
                    break;
                }
            }
            if (volumeName is not null) break;
        }

        if (volumeName is null)
            throw new InvalidOperationException(
                $"Container App {resourceGroup}/{appName} has no volume mounted at {options.ContainerDataPath}.");

        string? storageName = null;
        foreach (var volume in properties.GetProperty("template").GetProperty("volumes").EnumerateArray())
        {
            if (!string.Equals(volume.GetProperty("name").GetString(), volumeName, StringComparison.Ordinal)) continue;
            storageName = volume.GetProperty("storageName").GetString();
            break;
        }

        if (string.IsNullOrWhiteSpace(storageName))
            throw new InvalidOperationException($"Volume {volumeName} is not backed by environment storage.");

        var environment = AzureResourceId(environmentId);
        var storageUrl = $"https://management.azure.com/subscriptions/{subscription}/resourceGroups/{environment.ResourceGroup}/" +
                         $"providers/Microsoft.App/managedEnvironments/{environment.Name}/storages/{storageName}" +
                         "?api-version=2024-03-01";
        var storage = await RunArmReadWithEligibleRoleActivationAsync(
            options, identity, logger, storageUrl, ct).ConfigureAwait(false);

        using var storageJson = JsonDocument.Parse(storage);
        var azureFile = storageJson.RootElement.GetProperty("properties").GetProperty("azureFile");
        var accountName = azureFile.GetProperty("accountName").GetString();
        var shareName = azureFile.GetProperty("shareName").GetString();
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(shareName))
            throw new InvalidOperationException($"Environment storage {storageName} is not an Azure Files share.");

        return new AzureFilesSource(accountName, shareName);
    }

    private sealed record EligibleRole(string RoleDefinitionId, string EligibilityScheduleId);

    /// <summary>
    /// PIM is deliberately a response to an authorization failure, not a prerequisite or a
    /// start-up side effect. A developer who already has access sees no PIM traffic at all.
    /// </summary>
    private static async Task<string> RunArmReadWithEligibleRoleActivationAsync(
        UserDataSyncOptions options,
        AzureUserContext identity,
        ILogger logger,
        string url,
        CancellationToken ct)
    {
        try
        {
            return await SendArmAsync(identity.Credential, HttpMethod.Get, url, content: null, ct)
                .ConfigureAwait(false);
        }
        catch (AzureArmException ex) when (
            ex.StatusCode is 401 or 403 && options.EligibleAzureRoleDefinitionIds.Count > 0)
        {
            if (!identity.CanActivateEligibleRoles)
            {
                throw new InvalidOperationException(
                    $"Azure application {identity.DisplayName} cannot read the production resources. " +
                    "Assign its service principal Reader on the production resource group and " +
                    "Storage File Data Privileged Reader on the backing storage account. " + ex.Message,
                    ex);
            }

            logger.LogWarning(
                "Azure denied the production snapshot read; checking {Count} configured eligible PIM role(s)",
                options.EligibleAzureRoleDefinitionIds.Count);
        }

        await ActivateEligibleRolesAsync(options, identity, logger, ct).ConfigureAwait(false);

        // ARM authorization caches are eventually consistent. Retry the exact read which failed;
        // subsequent discovery calls only run after this proves the new assignment is effective.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var delay = TimeSpan.FromSeconds(3);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(delay, ct).ConfigureAwait(false);

            try
            {
                var result = await SendArmAsync(identity.Credential, HttpMethod.Get, url, content: null, ct)
                    .ConfigureAwait(false);
                logger.LogInformation("Azure PIM access is active; continuing the production snapshot");
                return result;
            }
            catch (AzureArmException ex) when (ex.StatusCode is 401 or 403 && DateTimeOffset.UtcNow < deadline)
            {
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.6, 12));
                continue;
            }
            catch (AzureArmException ex) when (ex.StatusCode is 401 or 403)
            {
                throw new InvalidOperationException(
                    "Eligible roles were activated, but Azure did not grant access within 90 seconds. " + ex.Message,
                    ex);
            }

            // Non-authorization failures should surface immediately.
        }
    }

    private static async Task ActivateEligibleRolesAsync(
        UserDataSyncOptions options, AzureUserContext identity, ILogger logger, CancellationToken ct)
    {
        var subscription = options.AzureSubscriptionId!;
        var scope = $"/subscriptions/{subscription}/resourceGroups/{options.AzureResourceGroup}";
        var principal = identity.PrincipalId;

        var eligibilityUrl =
            $"https://management.azure.com{scope}/providers/Microsoft.Authorization/" +
            "roleEligibilityScheduleInstances?api-version=2020-10-01&" +
            $"$filter=principalId%20eq%20'{principal}'";

        var eligibilityJson = await SendArmAsync(
            identity.Credential, HttpMethod.Get, eligibilityUrl, content: null, ct).ConfigureAwait(false);

        var eligible = ParseEligibleRoles(eligibilityJson);
        var missing = options.EligibleAzureRoleDefinitionIds
            .Where(wanted => !eligible.ContainsKey(wanted))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Azure denied the production snapshot and the signed-in user is not eligible for the required PIM role(s): " +
                string.Join(", ", missing) + $" at {scope}.");
        }

        var activeUrl =
            $"https://management.azure.com{scope}/providers/Microsoft.Authorization/" +
            "roleAssignmentScheduleInstances?api-version=2020-10-01&" +
            $"$filter=principalId%20eq%20'{principal}'";
        var activeJson = await SendArmAsync(
            identity.Credential, HttpMethod.Get, activeUrl, content: null, ct).ConfigureAwait(false);
        var active = ParseRoleDefinitionIds(activeJson);

        foreach (var roleDefinitionGuid in options.EligibleAzureRoleDefinitionIds)
        {
            if (active.Contains(roleDefinitionGuid))
            {
                logger.LogInformation("Azure role {RoleDefinitionId} is already active", roleDefinitionGuid);
                continue;
            }

            var role = eligible[roleDefinitionGuid];
            var requestId = Guid.NewGuid().ToString();
            var requestUrl =
                $"https://management.azure.com{scope}/providers/Microsoft.Authorization/" +
                $"roleAssignmentScheduleRequests/{requestId}?api-version=2020-10-01";
            var body = JsonSerializer.Serialize(new
            {
                properties = new
                {
                    principalId = principal,
                    roleDefinitionId = role.RoleDefinitionId,
                    requestType = "SelfActivate",
                    linkedRoleEligibilityScheduleId = role.EligibilityScheduleId,
                    justification = "Margin production support snapshot",
                    scheduleInfo = new
                    {
                        startDateTime = DateTimeOffset.UtcNow.ToString("O"),
                        expiration = new { type = "AfterDuration", duration = "PT1H" },
                    },
                },
            });

            logger.LogInformation("Activating eligible Azure role {RoleDefinitionId} for one hour", roleDefinitionGuid);
            string activation;
            try
            {
                activation = await SendArmAsync(
                    identity.Credential, HttpMethod.Put, requestUrl, body, ct).ConfigureAwait(false);
            }
            catch (AzureArmException ex)
            {
                if (IsAlreadyActive(ex.ResponseBody))
                {
                    logger.LogInformation("Azure role {RoleDefinitionId} is already active", roleDefinitionGuid);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Could not self-activate Azure PIM role {roleDefinitionGuid}. " +
                    "If the role requires approval or MFA, complete that in Azure PIM and run the sync again. " +
                    ex.Message,
                    ex);
            }

            using var result = JsonDocument.Parse(activation);
            var status = result.RootElement.TryGetProperty("properties", out var properties) &&
                         properties.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            if (!string.Equals(status, "Provisioned", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Azure PIM role {roleDefinitionGuid} was not activated immediately (status: {status ?? "unknown"}). " +
                    "Complete any approval or MFA requirement in Azure PIM, then run the sync again.");
            }
        }
    }

    private static Dictionary<string, EligibleRole> ParseEligibleRoles(string json)
    {
        using var document = JsonDocument.Parse(json);
        var roles = new Dictionary<string, EligibleRole>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("value", out var values)) return roles;

        foreach (var item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("properties", out var properties)) continue;
            var roleDefinitionId = properties.GetProperty("roleDefinitionId").GetString();
            var eligibilityScheduleId = properties.GetProperty("roleEligibilityScheduleId").GetString();
            if (string.IsNullOrWhiteSpace(roleDefinitionId) || string.IsNullOrWhiteSpace(eligibilityScheduleId)) continue;

            var roleGuid = roleDefinitionId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (Guid.TryParse(roleGuid, out var parsed))
                roles[parsed.ToString()] = new EligibleRole(roleDefinitionId, eligibilityScheduleId);
        }

        return roles;
    }

    private static HashSet<string> ParseRoleDefinitionIds(string json)
    {
        using var document = JsonDocument.Parse(json);
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("value", out var values)) return roles;

        foreach (var item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("properties", out var properties) ||
                !properties.TryGetProperty("roleDefinitionId", out var roleDefinition)) continue;

            var roleGuid = roleDefinition.GetString()?
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            if (Guid.TryParse(roleGuid, out var parsed)) roles.Add(parsed.ToString());
        }

        return roles;
    }

    private static bool IsAlreadyActive(string response) =>
        response.Contains("RoleAssignmentExists", StringComparison.OrdinalIgnoreCase) ||
        response.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
        response.Contains("already active", StringComparison.OrdinalIgnoreCase);

    private static ShareClient CreateShareClient(AzureFilesSource source, TokenCredential credential)
    {
        var clientOptions = new ShareClientOptions();
        clientOptions.AddPolicy(new FileRequestIntentPolicy(), HttpPipelinePosition.PerCall);
        return new ShareClient(
            new Uri($"https://{source.AccountName}.file.core.windows.net/{source.ShareName}"),
            credential,
            clientOptions);
    }

    private static async Task<T> RunAzureFilesWithEligibleRoleActivationAsync<T>(
        UserDataSyncOptions options,
        AzureUserContext identity,
        ILogger logger,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        try
        {
            return await operation(ct).ConfigureAwait(false);
        }
        catch (Azure.RequestFailedException ex) when (
            ex.Status is 401 or 403 && options.EligibleAzureRoleDefinitionIds.Count > 0)
        {
            if (!identity.CanActivateEligibleRoles)
            {
                throw new InvalidOperationException(
                    $"Azure application {identity.DisplayName} cannot read the production file share. " +
                    "Assign its service principal Storage File Data Privileged Reader on the backing storage account. " +
                    ex.Message,
                    ex);
            }

            logger.LogWarning(
                "Azure Files denied the production snapshot read; checking {Count} configured eligible PIM role(s)",
                options.EligibleAzureRoleDefinitionIds.Count);
            await ActivateEligibleRolesAsync(options, identity, logger, ct).ConfigureAwait(false);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var delay = TimeSpan.FromSeconds(3);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(delay, ct).ConfigureAwait(false);
            try
            {
                var result = await operation(ct).ConfigureAwait(false);
                logger.LogInformation("Azure PIM data access is active; continuing the production snapshot");
                return result;
            }
            catch (Azure.RequestFailedException ex) when (
                ex.Status is 401 or 403 && DateTimeOffset.UtcNow < deadline)
            {
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.6, 12));
            }
        }
    }

    private static async Task<IReadOnlyList<RemoteFile>> ListAzureFilesAsync(
        UserDataSyncOptions options,
        ShareClient share,
        CancellationToken ct)
    {
        var excluded = new HashSet<string>(options.Excludes, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in excluded)
        {
            if (entry.Contains('/') || entry.Contains('\\') || entry is "." or "..")
                throw new InvalidOperationException($"Exclude entries must be top-level names: {entry}");
        }

        var files = new List<RemoteFile>();
        var directories = new Queue<(ShareDirectoryClient Client, string Path)>();
        directories.Enqueue((share.GetRootDirectoryClient(), string.Empty));
        var listOptions = new ShareDirectoryGetFilesAndDirectoriesOptions
        {
            Traits = ShareFileTraits.Timestamps,
            IncludeExtendedInfo = true,
        };

        while (directories.TryDequeue(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var item in directory.Client.GetFilesAndDirectoriesAsync(listOptions, ct))
            {
                var path = directory.Path.Length == 0 ? item.Name : $"{directory.Path}/{item.Name}";
                ValidateRelativePath(path);
                var topLevel = path.Split('/', 2)[0];
                if (excluded.Contains(topLevel)) continue;

                if (item.IsDirectory)
                {
                    directories.Enqueue((directory.Client.GetSubdirectoryClient(item.Name), path));
                    continue;
                }

                var etag = item.Properties?.ETag.ToString() ?? string.Empty;
                var size = item.FileSize ?? 0;
                var lastModified = item.Properties?.LastModified?.ToString("O");
                if (etag.Length == 0 && lastModified is null)
                    throw new InvalidOperationException(
                        $"Azure Files did not return a version or timestamp for '{path}'; refusing an index that cannot detect changes.");

                // Azure Files' REST listing exposes ETag, but some SDK/service-version
                // combinations leave it empty on ShareFileItem. LastModified plus length is the
                // same remote-side change detector without adding one HEAD request per file.
                if (etag.Length == 0) etag = $"last-modified:{lastModified};size:{size}";
                files.Add(new RemoteFile(path, etag, size, lastModified, directory.Client.GetFileClient(item.Name)));
            }
        }

        return files;
    }

    private static async Task<SyncResult> BuildIncrementalAzureSnapshotAsync(
        UserDataSyncOptions options,
        string localPath,
        string staging,
        IReadOnlyList<RemoteFile> remoteFiles,
        SnapshotManifest? previous,
        ILogger logger,
        CancellationToken ct)
    {
        if (options.MaxConcurrentAzureDownloads < 1)
            throw new InvalidOperationException("MaxConcurrentAzureDownloads must be at least one.");

        if (previous is null)
        {
            logger.LogInformation("No compatible local manifest found; downloading the first complete indexed snapshot");
            await DownloadAzureFilesAsync(options, staging, remoteFiles, logger, ct)
                .ConfigureAwait(false);
            return new SyncResult(0, remoteFiles.Count, remoteFiles.Sum(file => file.Size), 0);
        }

        var reused = 0;
        var download = new List<RemoteFile>();
        foreach (var remote in remoteFiles)
        {
            ct.ThrowIfCancellationRequested();
            var sourcePath = LocalFile(localPath, remote.Path);
            var destinationPath = LocalFile(staging, remote.Path);
            if (previous.Files.TryGetValue(remote.Path, out var old) &&
                string.Equals(old.ETag, remote.ETag, StringComparison.Ordinal) &&
                File.Exists(sourcePath) &&
                string.Equals(await HashFileAsync(sourcePath, ct).ConfigureAwait(false), old.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                LinkOrCopy(sourcePath, destinationPath);
                reused++;
            }
            else
            {
                download.Add(remote);
            }
        }

        var remotePaths = remoteFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var removed = previous.Files.Keys.Count(path => !remotePaths.Contains(path));
        logger.LogInformation(
            "Incremental snapshot: {Reused} unchanged, {Downloaded} new/changed/local-modified, {Removed} removed",
            reused, download.Count, removed);

        using var gate = new SemaphoreSlim(options.MaxConcurrentAzureDownloads);
        var downloadedBytes = 0L;
        var tasks = download.Select(async remote =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await DownloadAzureFileAsync(staging, remote, ct).ConfigureAwait(false);
                Interlocked.Add(ref downloadedBytes, remote.Size);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new SyncResult(reused, download.Count, downloadedBytes, removed);
    }

    private static async Task DownloadAzureFilesAsync(
        UserDataSyncOptions options,
        string staging,
        IReadOnlyList<RemoteFile> remoteFiles,
        ILogger logger,
        CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(options.MaxConcurrentAzureDownloads);
        var completed = 0;
        await Task.WhenAll(remoteFiles.Select(async remote =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await DownloadAzureFileAsync(staging, remote, ct).ConfigureAwait(false);
                var done = Interlocked.Increment(ref completed);
                if (done % 250 == 0 || done == remoteFiles.Count)
                    logger.LogInformation("Downloaded {Completed}/{Total} production files", done, remoteFiles.Count);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
    }

    private static async Task DownloadAzureFileAsync(
        string staging,
        RemoteFile remote,
        CancellationToken ct)
    {
        var destination = LocalFile(staging, remote.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".download-{Guid.NewGuid():N}";

        try
        {
            var response = await remote.Client.DownloadAsync(cancellationToken: ct).ConfigureAwait(false);
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 128 * 1024, useAsync: true))
            {
                await response.Value.Content.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void LinkOrCopy(string source, string destination)
    {
        try
        {
            var linked = OperatingSystem.IsWindows()
                ? CreateHardLinkWindows(destination, source, IntPtr.Zero)
                : CreateHardLinkUnix(source, destination) == 0;
            if (!linked) throw new IOException($"Could not create hard link (error {Marshal.GetLastPInvokeError()}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            File.Copy(source, destination, overwrite: false);
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string newFileName, string existingFileName, IntPtr securityAttributes);

    private static string LocalFile(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
            throw new InvalidOperationException($"Remote path escapes the snapshot directory: {relativePath}");
        return fullPath;
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw new InvalidOperationException($"Invalid remote file path: {path}");
    }

    private sealed class FileRequestIntentPolicy : HttpPipelinePolicy
    {
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (!message.Request.Headers.Contains("x-ms-file-request-intent"))
                message.Request.Headers.Add("x-ms-file-request-intent", "backup");
            ProcessNext(message, pipeline);
        }

        public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (!message.Request.Headers.Contains("x-ms-file-request-intent"))
                message.Request.Headers.Add("x-ms-file-request-intent", "backup");
            return ProcessNextAsync(message, pipeline);
        }
    }

    private static (string ResourceGroup, string Name) AzureResourceId(string resourceId)
    {
        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var groupIndex = Array.FindIndex(segments, value =>
            value.Equals("resourceGroups", StringComparison.OrdinalIgnoreCase));
        var nameIndex = Array.FindLastIndex(segments, value =>
            value.Equals("managedEnvironments", StringComparison.OrdinalIgnoreCase));

        if (groupIndex < 0 || groupIndex + 1 >= segments.Length || nameIndex < 0 || nameIndex + 1 >= segments.Length)
            throw new InvalidOperationException($"Could not parse managed environment resource id: {resourceId}");

        return (segments[groupIndex + 1], segments[nameIndex + 1]);
    }

    private sealed class AzureArmException(HttpStatusCode statusCode, string responseBody, string requestUri)
        : InvalidOperationException(
            $"Azure ARM request to {requestUri} failed with HTTP {(int)statusCode} ({statusCode}). {responseBody}".Trim())
    {
        internal int StatusCode { get; } = (int)statusCode;
        internal string ResponseBody { get; } = responseBody;
    }

    private static async Task<string> SendArmAsync(
        TokenCredential credential,
        HttpMethod method,
        string url,
        string? content,
        CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([AzureUserContext.ArmScope]), ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (content is not null)
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

        using var response = await ArmClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AzureArmException(response.StatusCode, body, request.RequestUri!.AbsoluteUri);

        return body;
    }

    /// <summary>
    /// Asks the remote Docker daemon which volume the application's container mounts. The
    /// container's name carries a suffix that changes on every deploy, so it is the FQDN that is
    /// stable enough to configure against.
    /// </summary>
    private static async Task<string> ResolveCoolifyPathAsync(
        string host, UserDataSyncOptions options, CancellationToken ct)
    {
        // COOLIFY_FQDN is a comma-separated list, and its entries may carry a scheme and a
        // path ("share.example.com/,app.example.com"), so an exact match finds nothing on any
        // application that serves more than one hostname.
        var script = $$$"""
            set -euo pipefail
            target="{{{options.CoolifyFqdn}}}"
            for c in $(docker ps -q); do
              fqdns=$(docker inspect "$c" --format '{{range .Config.Env}}{{println .}}{{end}}' \
                        | sed -n 's/^COOLIFY_FQDN=//p' | head -1)
              [ -n "$fqdns" ] || continue
              hit=0
              old_ifs=$IFS; IFS=','
              for entry in $fqdns; do
                host=${entry#*://}
                host=${host%%/*}
                [ "$host" = "$target" ] && hit=1
              done
              IFS=$old_ifs
              [ "$hit" = 1 ] || continue
              docker inspect "$c" --format '{{range .Mounts}}{{if eq .Destination "{{{options.ContainerDataPath}}}"}}{{.Source}}{{end}}{{end}}'
              exit 0
            done
            exit 1
            """;

        var (exitCode, stdout, stderr) = await RunAsync("ssh", ["-o", "BatchMode=yes", host, "bash", "-s"], script, ct)
            .ConfigureAwait(false);

        var path = stdout.Trim();
        if (exitCode != 0 || path.Length == 0)
        {
            throw new InvalidOperationException(
                $"No running container on {host} serves {options.CoolifyFqdn} with a mount at " +
                $"{options.ContainerDataPath}. {stderr.Trim()}".Trim());
        }

        return path;
    }

    /// <summary>
    /// tar over ssh, straight into tar on this side — nothing lands as an archive in between.
    /// <para>
    /// Shelling out rather than using an SSH library is deliberate: keys, agents, known_hosts
    /// and jump hosts are already solved by the developer's own ssh configuration, and a library
    /// would solve all of it again, worse.
    /// </para>
    /// </summary>
    private static async Task StreamDirectoryAsync(
        string host, string remotePath, string destination, IReadOnlyList<string> excludes, CancellationToken ct)
    {
        var remoteTar = new StringBuilder($"tar -C '{remotePath}' -cz");
        foreach (var exclude in excludes) remoteTar.Append($" --exclude=./{exclude}");
        remoteTar.Append(" .");

        using var ssh = Process.Start(new ProcessStartInfo("ssh")
        {
            ArgumentList = { "-o", "BatchMode=yes", host, remoteTar.ToString() },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not start ssh.");

        using var tar = Process.Start(new ProcessStartInfo("tar")
        {
            ArgumentList = { "-C", destination, "-xz" },
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not start tar.");

        var sshErrors = ssh.StandardError.ReadToEndAsync(ct);
        var tarErrors = tar.StandardError.ReadToEndAsync(ct);

        try
        {
            await ssh.StandardOutput.BaseStream.CopyToAsync(tar.StandardInput.BaseStream, ct).ConfigureAwait(false);
            tar.StandardInput.Close();

            await Task.WhenAll(ssh.WaitForExitAsync(ct), tar.WaitForExitAsync(ct)).ConfigureAwait(false);
        }
        catch
        {
            await Task.WhenAll(TerminateProcessTreeAsync(ssh), TerminateProcessTreeAsync(tar))
                .ConfigureAwait(false);
            throw;
        }

        if (ssh.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh {host} failed ({ssh.ExitCode}): {(await sshErrors).Trim()}");
        }
        if (tar.ExitCode != 0)
        {
            throw new InvalidOperationException($"tar failed ({tar.ExitCode}): {(await tarErrors).Trim()}");
        }
    }

    private static int Redact(string root, IReadOnlyList<UserDataRedaction> redactions, ILogger logger)
    {
        var count = 0;

        foreach (var redaction in redactions)
        {
            var pattern = GlobToRegex(redaction.PathGlob);

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!pattern.IsMatch(relative)) continue;

                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json) continue;

                    var before = json.ToJsonString();
                    redaction.Redact(relative, json);
                    if (json.ToJsonString() == before) continue;

                    // Unchanged files may be hard-linked from the currently installed snapshot.
                    // Replacing rather than overwriting breaks that link, so redaction can never
                    // mutate the live snapshot while staging its successor.
                    var temporary = file + $".redacted-{Guid.NewGuid():N}";
                    try
                    {
                        File.WriteAllText(temporary, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        File.Move(temporary, file, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                    count++;
                }
                catch (JsonException)
                {
                    // A file that does not parse cannot be redacted, and shipping it unredacted
                    // is exactly what this class exists to prevent.
                    File.Delete(file);
                    logger.LogWarning("Deleted {File}: matched a redaction rule but is not valid JSON", relative);
                }
            }
        }

        return count;
    }

    /// <summary><c>*</c> matches within one path segment; <c>**</c> spans segments.</summary>
    private static Regex GlobToRegex(string glob)
    {
        // Segment-safe: "*" stops at a "/", "**" does not.
        var pattern = string.Join(".*", glob.Split("**").Select(part =>
            string.Join("[^/]*", part.Split('*').Select(Regex.Escape))));

        return new Regex($"^{pattern}$", RegexOptions.IgnoreCase);
    }

    private static async Task WriteMarkerAsync(
        string root,
        string source,
        string remotePath,
        UserDataSyncOptions options,
        SnapshotManifest? manifest,
        CancellationToken ct)
    {
        var marker = new JsonObject
        {
            ["source"] = "prod",
            ["host"] = source,
            ["remotePath"] = remotePath,
            ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["excluded"] = new JsonArray([.. options.Excludes.Select(e => (JsonNode)JsonValue.Create(e)!)]),
            ["redactions"] = new JsonArray([.. options.Redactions.Select(r => (JsonNode)JsonValue.Create(r.PathGlob)!)]),
            ["retainDays"] = options.RetainDays,
        };

        if (manifest is not null)
        {
            marker["manifestVersion"] = manifest.Version;
            marker["files"] = new JsonArray([.. manifest.Files.Values
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => (JsonNode)new JsonObject
                {
                    ["path"] = file.Path,
                    ["etag"] = file.ETag,
                    ["size"] = file.Size,
                    ["lastModified"] = file.LastModified,
                    ["sha256"] = file.Sha256,
                })]);
        }

        await File.WriteAllTextAsync(
            Path.Combine(root, MarkerFileName),
            marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
    }

    private static async Task<SnapshotManifest> CreateManifestAsync(
        string staging, IReadOnlyList<RemoteFile> remoteFiles, CancellationToken ct)
    {
        var files = new Dictionary<string, ManifestFile>(StringComparer.Ordinal);
        foreach (var remote in remoteFiles)
        {
            ct.ThrowIfCancellationRequested();
            var file = LocalFile(staging, remote.Path);
            if (!File.Exists(file))
                throw new InvalidOperationException($"Downloaded snapshot is missing {remote.Path}.");

            files[remote.Path] = new ManifestFile(
                remote.Path,
                remote.ETag,
                remote.Size,
                remote.LastModified,
                await HashFileAsync(file, ct).ConfigureAwait(false));
        }

        return new SnapshotManifest(ManifestVersion, files);
    }

    private static async Task<SnapshotManifest?> ReadCompatibleManifestAsync(
        string localPath,
        string source,
        string remotePath,
        UserDataSyncOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        var markerPath = Path.Combine(localPath, MarkerFileName);
        if (!File.Exists(markerPath)) return null;

        try
        {
            await using var stream = File.OpenRead(markerPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (ReadString(root, "source") != "prod" ||
                !string.Equals(ReadString(root, "host"), source, StringComparison.Ordinal) ||
                !string.Equals(ReadString(root, "remotePath"), remotePath, StringComparison.Ordinal) ||
                ReadInt64(root, "manifestVersion") != ManifestVersion ||
                !SameStrings(root, "excluded", options.Excludes) ||
                !SameStrings(root, "redactions", options.Redactions.Select(rule => rule.PathGlob)))
            {
                logger.LogInformation("The installed snapshot manifest is absent or incompatible; a full indexed sync is required");
                return null;
            }

            if (!root.TryGetProperty("files", out var fileArray) || fileArray.ValueKind != JsonValueKind.Array)
                return null;

            var files = new Dictionary<string, ManifestFile>(StringComparer.Ordinal);
            foreach (var item in fileArray.EnumerateArray())
            {
                var path = ReadString(item, "path");
                var etag = ReadString(item, "etag");
                var sha256 = ReadString(item, "sha256");
                var size = ReadInt64(item, "size");
                if (string.IsNullOrWhiteSpace(path) || etag is null || sha256 is null || size is null)
                    return null;

                ValidateRelativePath(path);
                files[path] = new ManifestFile(
                    path, etag, size.Value, ReadString(item, "lastModified"), sha256);
            }

            return new SnapshotManifest(ManifestVersion, files);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            logger.LogWarning("Could not reuse the installed snapshot manifest: {Message}", ex.Message);
            return null;
        }
    }

    private static bool SameStrings(JsonElement root, string propertyName, IEnumerable<string> expected)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return false;

        var actual = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        return actual.SequenceEqual(expected.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value)) return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Swaps the fetched copy in, keeping the previous one next to it.</summary>
    private static void Install(string staging, string localPath, int retainDays, ILogger logger)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

        if (Directory.Exists(localPath))
        {
            // Backing up a redacted copy of production to make room for a fresher redacted copy of
            // production buys nothing and costs a second pile of customer data — and with the
            // fetch wired to start-up, that pile grows once per restart. So the chain is only
            // kept for data somebody made: when the directory being replaced is itself a fetched
            // snapshot, the previous snapshot backups go instead of accumulating.
            if (IsSnapshot(localPath))
            {
                foreach (var stale in Backups(localPath).Where(IsSnapshot))
                {
                    Directory.Delete(stale, recursive: true);
                    logger.LogInformation("Replaced the previous snapshot backup {Backup}", Path.GetFileName(stale));
                }
            }

            var backup = $"{localPath}.bak-{stamp}";
            Directory.Move(localPath, backup);
            logger.LogInformation("Previous data kept at {Backup}", Path.GetFileName(backup));
        }

        Directory.Move(staging, localPath);
        PruneBackups(localPath, retainDays, logger);
    }

    /// <summary>True when a directory carries our marker, i.e. it was fetched rather than made.</summary>
    private static bool IsSnapshot(string directory) =>
        File.Exists(Path.Combine(directory, MarkerFileName));

    /// <summary>Every backup sitting beside <paramref name="localPath"/>, newest first.</summary>
    private static IEnumerable<string> Backups(string localPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(localPath));
        if (parent is null || !Directory.Exists(parent)) return [];

        return Directory
            .EnumerateDirectories(parent, Path.GetFileName(Path.GetFullPath(localPath)) + ".bak-*")
            .OrderByDescending(d => d)
            .ToList();
    }

    /// <summary>
    /// Copies of production expire. Retention is the difference between a debugging aid and an
    /// unmanaged second copy of the customer database.
    /// </summary>
    internal static void PruneBackups(string localPath, int retainDays, ILogger logger)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(localPath));
        if (parent is null || !Directory.Exists(parent)) return;

        var prefix = Path.GetFileName(Path.GetFullPath(localPath)) + ".bak-";
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retainDays);

        foreach (var directory in Directory.EnumerateDirectories(parent, prefix + "*"))
        {
            if (Directory.GetCreationTimeUtc(directory) > cutoff) continue;

            try
            {
                Directory.Delete(directory, recursive: true);
                logger.LogInformation("Deleted {Backup}, older than {Days} days", Path.GetFileName(directory), retainDays);
            }
            catch (IOException ex)
            {
                logger.LogWarning("Could not delete {Backup}: {Message}", Path.GetFileName(directory), ex.Message);
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, string[] arguments, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!process.HasExited)
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // It either exited between HasExited and Kill, or the OS is still reaping it. The
            // unique staging directory prevents a subsequent sync from colliding in either case.
        }
    }
}
