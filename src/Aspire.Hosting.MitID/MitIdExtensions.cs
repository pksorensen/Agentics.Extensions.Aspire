using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Wires the MitID test-user service (<c>agent-mitid</c>) into an AppHost, so a
/// test project can approve a MitID pre-production login without a phone.
/// </summary>
/// <remarks>
/// <para>
/// This adds no container and starts no process. agent-mitid keeps a registry of
/// named test users that outlives any one AppHost run — the point of it is that
/// the same <c>commuteconnects/Oscar39838</c> exists tomorrow, on the build
/// server, and in someone else's checkout. So the integration is a reference to a
/// running instance, not a copy of one.
/// </para>
/// <para>
/// What it does is put <c>MITID_SERVICE_URL</c> and <c>MITID_MCP_TOKEN</c> on the
/// projects that ask for them, which is exactly what
/// <c>MitIdApprover.FromEnvironment()</c> in <c>Agentics.MitID.Testing</c> reads.
/// A test then needs no configuration of its own, and a machine with no token
/// still works — the approver falls back to driving pp.mitid.dk directly.
/// </para>
/// </remarks>
public static class MitIdExtensions
{
    /// <summary>Environment variable carrying the service origin.</summary>
    public const string ServiceUrlVariable = "MITID_SERVICE_URL";

    /// <summary>Environment variable carrying the MCP bearer token.</summary>
    public const string TokenVariable = "MITID_MCP_TOKEN";

    /// <summary>Environment variable carrying the default project key.</summary>
    public const string ProjectVariable = "MITID_PROJECT";

    /// <summary>
    /// Registers the MitID test-user service with the AppHost.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="configure">Optional configuration; otherwise environment and configuration are read.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the service is unconfigured and <see cref="MitIdOptions.Optional"/> is false.
    /// </exception>
    public static IDistributedApplicationBuilder AddMitIdTestUsers(
        this IDistributedApplicationBuilder builder,
        Action<MitIdOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new MitIdOptions();
        configure?.Invoke(options);

        options.ServiceUrl ??= builder.Configuration["MitID:ServiceUrl"]
            ?? Environment.GetEnvironmentVariable(ServiceUrlVariable);
        options.Token ??= builder.Configuration["MitID:Token"]
            ?? Environment.GetEnvironmentVariable(TokenVariable);
        options.Project ??= builder.Configuration["MitID:Project"];

        if (!options.Optional && (string.IsNullOrWhiteSpace(options.ServiceUrl) || string.IsNullOrWhiteSpace(options.Token)))
        {
            throw new InvalidOperationException(
                "MitID test-user service is not configured. Set MitID:ServiceUrl and MitID:Token (user secrets), " +
                $"or the {ServiceUrlVariable} and {TokenVariable} environment variables. " +
                "Read the token from the service PWA's Indstillinger tab.");
        }

        builder.Services.AddSingleton(options);
        return builder;
    }

    /// <summary>
    /// Gives a resource the MitID environment variables, so anything in it that
    /// calls <c>MitIdApprover.FromEnvironment()</c> uses the registry.
    /// </summary>
    /// <remarks>
    /// Injects nothing when the service is unconfigured — a test then falls back
    /// to direct mode and still passes, which is the behaviour you want on a
    /// laptop that has never been given a token.
    /// </remarks>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource to configure.</param>
    /// <param name="options">
    /// Options to use instead of the ones registered by <see cref="AddMitIdTestUsers"/>.
    /// </param>
    /// <returns>The resource builder, for chaining.</returns>
    public static IResourceBuilder<T> WithMitIdTestUsers<T>(
        this IResourceBuilder<T> builder,
        MitIdOptions? options = null)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Read the registration back out of the descriptor rather than building a
        // provider: the AppHost's container is not finished being configured yet,
        // and building one here would snapshot a half-built graph.
        options ??= builder.ApplicationBuilder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(MitIdOptions))?.ImplementationInstance as MitIdOptions
            ?? new MitIdOptions();

        if (string.IsNullOrWhiteSpace(options.ServiceUrl) || string.IsNullOrWhiteSpace(options.Token))
        {
            return builder;
        }

        builder.WithEnvironment(ServiceUrlVariable, options.ServiceUrl.TrimEnd('/'));
        builder.WithEnvironment(TokenVariable, options.Token);
        if (!string.IsNullOrWhiteSpace(options.Project))
        {
            builder.WithEnvironment(ProjectVariable, options.Project);
        }
        return builder;
    }
}
