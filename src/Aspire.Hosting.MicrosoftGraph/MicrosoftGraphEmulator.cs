using Aspire.Hosting.MicrosoftTenant.Emulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Aspire.Hosting.MicrosoftGraph.Emulator;

public sealed class MicrosoftGraphState
{
    private int _counter;
    private readonly string? _statePath;
    private readonly object _saveGate = new();
    public ConcurrentDictionary<string, GraphApplication> Applications { get; } = [];
    public ConcurrentDictionary<string, GraphServicePrincipal> ServicePrincipals { get; } = [];

    public MicrosoftGraphState(MicrosoftTenantState tenant, IConfiguration configuration)
    {
        var dataDirectory = configuration["USER_DATA_DIR"];
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            _statePath = Path.Combine(dataDirectory, "microsoft-graph.json");
            if (File.Exists(_statePath))
            {
                var persisted = JsonSerializer.Deserialize<PersistedGraphState>(File.ReadAllText(_statePath));
                if (persisted is not null)
                {
                    _counter = persisted.Counter;
                    foreach (var item in persisted.Applications)
                    {
                        var application = new GraphApplication
                        {
                            Id = item.Id,
                            AppId = item.AppId,
                            DisplayName = item.DisplayName,
                        };
                        application.PasswordKeyIds.UnionWith(item.PasswordKeyIds);
                        // Null in files written before federated credentials existed.
                        if (item.FederatedIdentityCredentials is { } credentials)
                            application.FederatedIdentityCredentials.AddRange(credentials);
                        Applications[application.Id] = application;
                    }
                    foreach (var item in persisted.ServicePrincipals)
                    {
                        ServicePrincipals[item.Id] = new GraphServicePrincipal
                        {
                            Id = item.Id,
                            AppId = item.AppId,
                            AccountEnabled = item.AccountEnabled,
                        };
                    }
                }
            }
        }

        foreach (var seeded in tenant.Applications)
        {
            if (Applications.Values.Any(item => item.AppId == seeded.ClientId)) continue;
            var application = new GraphApplication
            {
                Id = $"seed-{seeded.ClientId}",
                AppId = seeded.ClientId,
                DisplayName = seeded.DisplayName,
            };
            Applications[application.Id] = application;
        }

        Save();
    }

    public string Next(string prefix) => $"{prefix}-{Interlocked.Increment(ref _counter)}";

    public void Save()
    {
        if (_statePath is null) return;
        lock (_saveGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var snapshot = new PersistedGraphState(
                _counter,
                Applications.Values.Select(item => new PersistedGraphApplication(
                    item.Id, item.AppId, item.DisplayName, item.PasswordKeyIds.ToArray(),
                    item.FederatedIdentityCredentialsSnapshot())).ToArray(),
                ServicePrincipals.Values.Select(item => new PersistedGraphServicePrincipal(
                    item.Id, item.AppId, item.AccountEnabled)).ToArray());
            var temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            File.Move(temporary, _statePath, overwrite: true);
        }
    }

    private sealed record PersistedGraphState(
        int Counter,
        IReadOnlyList<PersistedGraphApplication> Applications,
        IReadOnlyList<PersistedGraphServicePrincipal> ServicePrincipals);
    private sealed record PersistedGraphApplication(
        string Id,
        string AppId,
        string DisplayName,
        IReadOnlyList<string> PasswordKeyIds,
        IReadOnlyList<GraphFederatedIdentityCredential>? FederatedIdentityCredentials = null);
    private sealed record PersistedGraphServicePrincipal(string Id, string AppId, bool AccountEnabled);
}

public sealed class GraphApplication
{
    public required string Id { get; init; }
    public required string AppId { get; init; }
    public required string DisplayName { get; init; }
    public HashSet<string> PasswordKeyIds { get; } = [];
    public List<GraphFederatedIdentityCredential> FederatedIdentityCredentials { get; } = [];

    internal GraphFederatedIdentityCredential[] FederatedIdentityCredentialsSnapshot()
    {
        lock (FederatedIdentityCredentials) return [.. FederatedIdentityCredentials];
    }
}

/// <summary>A federated identity credential as Graph returns it.</summary>
public sealed record GraphFederatedIdentityCredential(
    string Id,
    string Name,
    string Issuer,
    string Subject,
    IReadOnlyList<string> Audiences,
    string? Description);

public sealed class GraphServicePrincipal
{
    public required string Id { get; init; }
    public required string AppId { get; init; }
    public bool AccountEnabled { get; set; } = true;
}

public static class MicrosoftGraphEmulatorExtensions
{
    public static IServiceCollection AddMicrosoftGraphEmulator(this IServiceCollection services)
    {
        services.AddSingleton<MicrosoftGraphState>();
        return services;
    }

    public static IEndpointRouteBuilder MapMicrosoftGraphEmulator(this IEndpointRouteBuilder endpoints)
    {
        MapVersion(endpoints, "/graph/v1.0");
        MapVersion(endpoints, "/graph/beta");
        return endpoints;
    }

    private static void MapVersion(IEndpointRouteBuilder endpoints, string root)
    {
        endpoints.MapGet($"{root}/applications", (
            HttpRequest request, MicrosoftGraphState graph) =>
        {
            var name = FilterValue(request.Query["$filter"]!);
            return Results.Ok(new
            {
                value = graph.Applications.Values
                    .Where(app => name is null || app.DisplayName == name)
                    .Select(DescribeApplication),
            });
        });

        endpoints.MapPost($"{root}/applications", (
            JsonElement body, MicrosoftGraphState graph) =>
        {
            var app = new GraphApplication
            {
                Id = graph.Next("obj"),
                AppId = Guid.NewGuid().ToString(),
                DisplayName = body.TryGetProperty("displayName", out var displayName)
                    ? displayName.GetString() ?? ""
                    : "",
            };
            graph.Applications[app.Id] = app;
            graph.Save();
            return Results.Json(DescribeApplication(app), statusCode: 201);
        });

        endpoints.MapGet($"{root}/servicePrincipals", (
            HttpRequest request, MicrosoftGraphState graph) =>
        {
            var appId = FilterValue(request.Query["$filter"]!);
            return Results.Ok(new
            {
                value = graph.ServicePrincipals.Values
                    .Where(sp => appId is null || sp.AppId == appId)
                    .Select(DescribeServicePrincipal),
            });
        });

        endpoints.MapPost($"{root}/servicePrincipals", (
            JsonElement body, MicrosoftGraphState graph) =>
        {
            var appId = body.GetProperty("appId").GetString() ?? "";
            if (!graph.Applications.Values.Any(app => app.AppId == appId))
                return GraphNotFound("Request_ResourceNotFound", "backing application not found");

            var sp = new GraphServicePrincipal { Id = graph.Next("sp"), AppId = appId };
            graph.ServicePrincipals[sp.Id] = sp;
            graph.Save();
            return Results.Json(DescribeServicePrincipal(sp), statusCode: 201);
        });

        endpoints.MapPost($"{root}/applications/{{id}}/addPassword", (
            string id, JsonElement body, MicrosoftGraphState graph) =>
        {
            if (!graph.Applications.TryGetValue(id, out var app))
                return GraphNotFound("Request_ResourceNotFound", "application not found");

            var input = body.GetProperty("passwordCredential");
            var keyId = Guid.NewGuid().ToString();
            app.PasswordKeyIds.Add(keyId);
            graph.Save();
            return Results.Ok(new
            {
                keyId,
                secretText = $"local-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))}",
                displayName = input.GetProperty("displayName").GetString(),
                startDateTime = DateTimeOffset.UtcNow,
                endDateTime = input.GetProperty("endDateTime").GetDateTimeOffset(),
            });
        });

        endpoints.MapPost($"{root}/applications/{{id}}/removePassword", (
            string id, JsonElement body, MicrosoftGraphState graph) =>
        {
            if (!graph.Applications.TryGetValue(id, out var app))
                return GraphNotFound("Request_ResourceNotFound", "application not found");
            app.PasswordKeyIds.Remove(body.GetProperty("keyId").GetString() ?? "");
            graph.Save();
            return Results.NoContent();
        });

        endpoints.MapGet($"{root}/applications/{{id}}/federatedIdentityCredentials", (
            string id, MicrosoftGraphState graph) =>
        {
            if (!graph.Applications.TryGetValue(id, out var app))
                return GraphNotFound("Request_ResourceNotFound", "application not found");
            return Results.Ok(new { value = app.FederatedIdentityCredentialsSnapshot() });
        });

        // Graph refuses a second credential with the same name, and a second with the same
        // issuer+subject pair, on one application. The emulator does both, so a caller that forgot
        // to list-and-adopt fails here rather than first in a real tenant.
        endpoints.MapPost($"{root}/applications/{{id}}/federatedIdentityCredentials", (
            string id, JsonElement body, MicrosoftGraphState graph) =>
        {
            if (!graph.Applications.TryGetValue(id, out var app))
                return GraphNotFound("Request_ResourceNotFound", "application not found");

            var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
            var issuer = body.TryGetProperty("issuer", out var i) ? i.GetString() : null;
            var subject = body.TryGetProperty("subject", out var s) ? s.GetString() : null;
            var audiences = body.TryGetProperty("audiences", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(item => item.GetString() ?? "").ToArray()
                : [];
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(issuer) ||
                string.IsNullOrWhiteSpace(subject) || audiences.Length != 1)
                return GraphBadRequest("name, issuer, subject and exactly one audience are required");

            var credential = new GraphFederatedIdentityCredential(
                Guid.NewGuid().ToString(),
                name,
                issuer,
                subject,
                audiences,
                body.TryGetProperty("description", out var d) ? d.GetString() : null);

            lock (app.FederatedIdentityCredentials)
            {
                if (app.FederatedIdentityCredentials.Any(item => item.Name == name))
                    return GraphBadRequest($"a federated identity credential named '{name}' already exists");
                if (app.FederatedIdentityCredentials.Any(item => item.Issuer == issuer && item.Subject == subject))
                    return GraphBadRequest("a federated identity credential with this issuer and subject already exists");
                if (app.FederatedIdentityCredentials.Count >= 20)
                    return GraphBadRequest("an application can hold at most 20 federated identity credentials");
                app.FederatedIdentityCredentials.Add(credential);
            }

            graph.Save();
            return Results.Json(credential, statusCode: 201);
        });

        endpoints.MapPatch($"{root}/servicePrincipals/{{id}}", (
            string id, JsonElement body, MicrosoftGraphState graph) =>
        {
            if (!graph.ServicePrincipals.TryGetValue(id, out var sp))
                return GraphNotFound("Request_ResourceNotFound", "service principal not found");
            if (body.TryGetProperty("accountEnabled", out var enabled))
                sp.AccountEnabled = enabled.GetBoolean();
            graph.Save();
            return Results.NoContent();
        });

        endpoints.MapDelete($"{root}/applications/{{id}}", (
            string id, MicrosoftGraphState graph) =>
        {
            if (!graph.Applications.TryRemove(id, out var app))
                return GraphNotFound("Request_ResourceNotFound", "application not found");
            foreach (var sp in graph.ServicePrincipals.Where(pair => pair.Value.AppId == app.AppId))
                graph.ServicePrincipals.TryRemove(sp.Key, out _);
            graph.Save();
            return Results.NoContent();
        });
    }

    private static string? FilterValue(string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return null;
        var open = filter.IndexOf('\'');
        var close = filter.LastIndexOf('\'');
        return open >= 0 && close > open ? filter[(open + 1)..close].Replace("''", "'") : null;
    }

    private static object DescribeApplication(GraphApplication app) => new
    {
        id = app.Id,
        appId = app.AppId,
        displayName = app.DisplayName,
    };

    private static object DescribeServicePrincipal(GraphServicePrincipal sp) => new
    {
        id = sp.Id,
        appId = sp.AppId,
        accountEnabled = sp.AccountEnabled,
    };

    private static IResult GraphNotFound(string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: 404);

    private static IResult GraphBadRequest(string message) =>
        Results.Json(new { error = new { code = "Request_BadRequest", message } }, statusCode: 400);
}
