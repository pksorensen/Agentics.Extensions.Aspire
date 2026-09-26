using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Destructive, local-only dashboard commands for Next.js development resources.
/// </summary>
public static class NextJsDashboardCommandExtensions
{
    /// <summary>
    /// Adds a dashboard command that replaces a local user-data directory with
    /// an empty directory, keeps the previous contents beside it as a backup,
    /// and restarts the resource.
    /// </summary>
    public static IResourceBuilder<TResource> WithClearUserDataCommand<TResource>(
        this IResourceBuilder<TResource> builder,
        string userDataDirectory)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode) return builder;

        var target = ValidateUserDataDirectory(userDataDirectory);

        return builder.WithCommand(
            "clear-user-data",
            "Clear User Data",
            context => RunWhileStoppedAsync(
                context,
                () =>
                {
                    string? backup = null;
                    if (Directory.Exists(target))
                    {
                        backup = NextAvailableBackupPath(target);
                        Directory.Move(target, backup);
                    }

                    Directory.CreateDirectory(target);
                    return backup is null
                        ? $"Created an empty user-data directory at {target}."
                        : $"Cleared user data and kept the previous contents in {backup}.";
                }),
            new CommandOptions
            {
                IconName = "DatabaseLightning",
                IconVariant = IconVariant.Regular,
                Description = "Replace local user data with an empty directory, keep a backup, and restart Next.js",
                ConfirmationMessage = "Clear the local user data and restart this resource? The current contents are kept as a backup.",
                IsHighlighted = true,
            });
    }

    /// <summary>
    /// Adds a dashboard command that stops the resource, deletes generated
    /// Next.js output directories, and starts the resource again.
    /// </summary>
    public static IResourceBuilder<TResource> WithClearNextBuildCommand<TResource>(
        this IResourceBuilder<TResource> builder,
        string workingDirectory,
        params string[] buildDirectoryNames)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode) return builder;

        var targets = ResolveBuildDirectories(workingDirectory, buildDirectoryNames);

        return builder.WithCommand(
            "clear-next-build",
            "Clear .next & Restart",
            context => RunWhileStoppedAsync(
                context,
                () =>
                {
                    var removed = new List<string>();
                    foreach (var target in targets)
                    {
                        if (!Directory.Exists(target)) continue;

                        Directory.Delete(target, recursive: true);
                        removed.Add(Path.GetFileName(target));
                    }

                    return removed.Count == 0
                        ? "No Next.js build directories existed; restarted the resource."
                        : $"Deleted {string.Join(", ", removed)} and restarted the resource.";
                }),
            new CommandOptions
            {
                IconName = "BroomSparkle",
                IconVariant = IconVariant.Regular,
                Description = "Delete generated Next.js build directories and restart Next.js",
                ConfirmationMessage = $"Delete {string.Join(", ", targets.Select(Path.GetFileName))} and restart this resource?",
                IsHighlighted = true,
            });
    }

    private static async Task<ExecuteCommandResult> RunWhileStoppedAsync(
        ExecuteCommandContext context,
        Func<string> mutate)
    {
        var commands = context.Services.GetRequiredService<ResourceCommandService>();

        try
        {
            await commands.ExecuteCommandAsync(
                context.ResourceName,
                KnownResourceCommands.StopCommand,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return CommandResults.Failure(error);
        }

        string? message = null;
        Exception? mutationError = null;
        try
        {
            message = await Task.Run(mutate, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            mutationError = error;
        }

        try
        {
            await commands.ExecuteCommandAsync(
                context.ResourceName,
                KnownResourceCommands.StartCommand,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception restartError)
        {
            return mutationError is null
                ? CommandResults.Failure(restartError)
                : CommandResults.Failure(new AggregateException(mutationError, restartError));
        }

        return mutationError is null
            ? CommandResults.Success(message!)
            : CommandResults.Failure(mutationError);
    }

    private static string ValidateUserDataDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A user-data directory is required.", nameof(directory));

        var target = Path.GetFullPath(directory);
        if (string.Equals(target, Path.GetPathRoot(target), PathComparison))
            throw new ArgumentException("The filesystem root cannot be used as a user-data directory.", nameof(directory));
        if (Directory.GetParent(target) is null)
            throw new ArgumentException("The user-data directory must have a parent directory.", nameof(directory));

        return target;
    }

    private static IReadOnlyList<string> ResolveBuildDirectories(
        string workingDirectory,
        IReadOnlyCollection<string> directoryNames)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("A Next.js working directory is required.", nameof(workingDirectory));

        var root = Path.GetFullPath(workingDirectory);
        var names = directoryNames.Count == 0 ? [".next"] : directoryNames;
        var targets = new List<string>(names.Count);

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !string.Equals(name, Path.GetFileName(name), PathComparison) ||
                !name.StartsWith(".next", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Next.js build directories must be direct child names beginning with '.next'.",
                    nameof(directoryNames));
            }

            targets.Add(Path.GetFullPath(Path.Combine(root, name)));
        }

        return targets.Distinct(PathComparer).ToArray();
    }

    private static string NextAvailableBackupPath(string target)
    {
        var prefix = $"{target}.bak-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
        var candidate = prefix;
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            candidate = $"{prefix}-{suffix}";

        return candidate;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
