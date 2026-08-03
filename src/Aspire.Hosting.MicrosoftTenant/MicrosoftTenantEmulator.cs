using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Aspire.Hosting.MicrosoftTenant.Emulator;

public sealed class MicrosoftTenantState
{
    private readonly ConcurrentDictionary<string, MicrosoftTenantAppRegistration> _applications =
        new(StringComparer.OrdinalIgnoreCase);

    public MicrosoftTenantState(MicrosoftTenantSeed seed)
    {
        PrimaryDomain = seed.PrimaryDomain;
        TenantId = seed.TenantId;
        foreach (var app in seed.Applications) _applications[app.ClientId] = app;
    }

    public string PrimaryDomain { get; }
    public string TenantId { get; }
    public IReadOnlyCollection<MicrosoftTenantAppRegistration> Applications => _applications.Values.ToArray();

    public bool ValidateClient(string clientId, string secret) =>
        _applications.TryGetValue(clientId, out var app) &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(app.ClientSecret),
            System.Text.Encoding.UTF8.GetBytes(secret));
}

public static class MicrosoftTenantEmulatorExtensions
{
    public static IServiceCollection AddMicrosoftTenantEmulator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var json = configuration["MICROSOFT_TENANT_SEED_JSON"];
        var seed = string.IsNullOrWhiteSpace(json)
            ? new MicrosoftTenantSeed(
                configuration["MICROSOFT_TENANT_DOMAIN"] ?? "example.test",
                configuration["MICROSOFT_TENANT_ID"] ?? Guid.Empty.ToString(),
                [])
            : JsonSerializer.Deserialize<MicrosoftTenantSeed>(json)
                ?? throw new InvalidOperationException("MICROSOFT_TENANT_SEED_JSON is invalid.");

        services.AddSingleton(seed);
        services.AddSingleton<MicrosoftTenantState>();
        return services;
    }

    public static IEndpointRouteBuilder MapMicrosoftTenantEmulator(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", (MicrosoftTenantState tenant) => Results.Ok(new
        {
            status = "ok",
            tenantId = tenant.TenantId,
            primaryDomain = tenant.PrimaryDomain,
        }));

        endpoints.MapPost("/login/{tenantId}/oauth2/v2.0/token", IssueToken);
        endpoints.MapPost("/{tenantId}/oauth2/v2.0/token", IssueToken);
        return endpoints;
    }

    private static async Task<IResult> IssueToken(
        string tenantId,
        HttpRequest request,
        MicrosoftTenantState tenant)
    {
        if (!string.Equals(tenantId, tenant.TenantId, StringComparison.OrdinalIgnoreCase))
            return OAuthError("invalid_request", $"AADSTS90002: tenant '{tenantId}' not found");

        var form = await request.ReadFormAsync();
        var clientId = form["client_id"].ToString();
        var secret = form["client_secret"].ToString();
        if (form["grant_type"] != "client_credentials" ||
            string.IsNullOrWhiteSpace(clientId) ||
            !tenant.ValidateClient(clientId, secret))
            return OAuthError("invalid_client", "AADSTS7000215: invalid client secret");

        return Results.Ok(new
        {
            token_type = "Bearer",
            expires_in = 3600,
            access_token = $"tenant:{tenant.TenantId}:app:{clientId}:scope:{form["scope"]}",
        });
    }

    private static IResult OAuthError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: 401);
}
