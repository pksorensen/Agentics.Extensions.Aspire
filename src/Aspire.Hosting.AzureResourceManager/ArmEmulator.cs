using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Aspire.Hosting.AzureResourceManager.Emulator;

internal sealed record ArmProviderRegistration(string ProviderNamespace, Type HandlerType);

public sealed class AzureResourceManagerState
{
    private readonly string? _statePath;
    private readonly object _saveGate = new();

    public AzureResourceManagerState(IConfiguration configuration)
    {
        var json = configuration["AZURE_RESOURCE_MANAGER_EMULATOR_JSON"];
        Seed = string.IsNullOrWhiteSpace(json)
            ? new AzureResourceManagerSeed(false, [])
            : JsonSerializer.Deserialize<AzureResourceManagerSeed>(json)
                ?? new AzureResourceManagerSeed(false, []);

        var dataDirectory = configuration["USER_DATA_DIR"];
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            _statePath = Path.Combine(dataDirectory, "azure-resource-manager.json");
            if (File.Exists(_statePath))
            {
                var persisted = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(_statePath));
                if (persisted is not null)
                    foreach (var item in persisted) Resources[item.Key] = item.Value;
            }
        }
    }

    public AzureResourceManagerSeed Seed { get; }
    public ConcurrentDictionary<string, JsonElement> Resources { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void Save()
    {
        if (_statePath is null) return;
        lock (_saveGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Resources));
            File.Move(temporary, _statePath, overwrite: true);
        }
    }
}

public static class AzureResourceManagerEmulatorExtensions
{
    public static IServiceCollection AddAzureResourceManagerEmulator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<AzureResourceManagerState>();
        services.RegisterArmProviderEmulator<MicrosoftAuthorizationProviderEmulator>("Microsoft.Authorization");
        services.RegisterArmProviderEmulator<MicrosoftCommunicationProviderEmulator>("Microsoft.Communication");
        return services;
    }

    public static IServiceCollection RegisterArmProviderEmulator<T>(
        this IServiceCollection services,
        string providerNamespace)
        where T : class, IArmProviderEmulator
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerNamespace);
        services.AddSingleton<T>();
        services.AddSingleton(new ArmProviderRegistration(providerNamespace, typeof(T)));
        return services;
    }

    public static IEndpointRouteBuilder MapAzureResourceManagerEmulator(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/arm/{**resourcePath}", ["GET", "PUT", "PATCH", "DELETE", "POST"],
            DispatchAsync);
        return endpoints;
    }

    private static async Task<IResult> DispatchAsync(
        string resourcePath,
        HttpRequest http,
        IServiceProvider services,
        IEnumerable<ArmProviderRegistration> registrations,
        CancellationToken cancellationToken)
    {
        var decoded = "/" + resourcePath.TrimStart('/');
        // Extension resources (for example a role assignment scoped to an ACS
        // resource) contain more than one provider segment. ARM dispatches the
        // request to the final namespace, not the resource provider owning the
        // parent scope.
        var marker = decoded.LastIndexOf("/providers/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return ToResult(ArmProviderResponse.NotFound($"'{decoded}' has no provider namespace."));

        var after = decoded[(marker + "/providers/".Length)..];
        var slash = after.IndexOf('/');
        var providerNamespace = slash < 0 ? after : after[..slash];
        var providerPath = slash < 0 ? "" : after[(slash + 1)..];

        JsonElement? body = null;
        if (http.ContentLength is > 0)
            body = (await JsonDocument.ParseAsync(http.Body, cancellationToken: cancellationToken)).RootElement.Clone();

        var request = new ArmProviderRequest(
            http.Method,
            decoded,
            providerNamespace,
            providerPath,
            http.Query["api-version"].ToString(),
            body);

        foreach (var registration in registrations.Where(item =>
                     string.Equals(item.ProviderNamespace, providerNamespace, StringComparison.OrdinalIgnoreCase)))
        {
            var handler = (IArmProviderEmulator)services.GetRequiredService(registration.HandlerType);
            if (await handler.HandleAsync(request, cancellationToken) is { } response)
                return ToResult(response);
        }

        return ToResult(ArmProviderResponse.NotFound(
            $"No emulator handles '{providerNamespace}/{providerPath}'."));
    }

    private static IResult ToResult(ArmProviderResponse response) =>
        response.StatusCode == 204
            ? Results.NoContent()
            : Results.Json(response.Body, statusCode: response.StatusCode);
}
