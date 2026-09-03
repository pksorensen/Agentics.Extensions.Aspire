using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Aspire.Hosting.UserData;

namespace Aspire.Hosting;

/// <summary>
/// Wires a file-backed resource to a local data directory, and puts fetching production's data
/// into that directory behind a dashboard button.
/// </summary>
public static class UserDataExtensions
{
    /// <summary>
    /// Points <paramref name="resource"/> at <paramref name="localPath"/> and adds three
    /// commands to it: <em>Fetch production data</em>, <em>Reset to empty</em> and
    /// <em>Restore previous data</em>.
    /// <para>
    /// Fetching is a command rather than something this call performs, and that is the whole
    /// design. Pulling a copy of production must be an act someone chooses, in front of a
    /// confirmation dialog — not a side effect of starting the AppHost, which would put customer
    /// data on the disk of everyone who ever ran the solution.
    /// </para>
    /// <para>
    /// The fetched copy carries a <c>.snapshot-origin.json</c> marker. Reading it and refusing to
    /// do anything that reaches real people — sending mail above all — is the application's job;
    /// this package only guarantees the marker is there.
    /// </para>
    /// </summary>
    /// <param name="resource">The resource that reads the data directory.</param>
    /// <param name="localPath">Local data directory, absolute or relative to the AppHost.</param>
    /// <param name="configure">Where production lives, and what to strip out of a copy of it.</param>
    public static IResourceBuilder<T> WithUserData<T>(
        this IResourceBuilder<T> resource,
        string localPath,
        Action<UserDataSyncOptions> configure)
        where T : class, IResource, IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new UserDataSyncOptions();
        configure(options);

        if (options.Redactions.Count == 0)
        {
            throw new InvalidOperationException(
                "WithUserData requires at least one Redact rule. A copy of production carries real " +
                "credentials and real personal data; name what must be stripped out of it before it " +
                "reaches a developer machine. Redact(\"logins/*/credentials.json\", j => j.Remove(\"passwordHash\")) " +
                "is the shape.");
        }

        // A test run points the app at its own isolated directory; honour that over the default so
        // adopting this does not quietly break test isolation.
        var configured = resource.ApplicationBuilder.Configuration[options.LocalPathConfigurationKey];
        var path = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configured) ? localPath : configured,
            resource.ApplicationBuilder.AppHostDirectory);

        resource.WithEnvironment(options.EnvironmentVariable, path);

        resource.WithCommand(
            "fetch-production-data",
            "Fetch production data",
            async context =>
            {
                try
                {
                    await RemoteUserData.FetchAsync(options, path, context.Logger, context.CancellationToken)
                        .ConfigureAwait(false);

                    return new ExecuteCommandResult
                    {
                        Success = true,
                        // The store is read per request in the apps this exists for, so a restart is
                        // usually unnecessary — say so rather than let everyone guess.
                        Message = $"Production data copied to {path}. Restart the resource if it caches on boot.",
                    };
                }
                catch (OperationCanceledException)
                {
                    return new ExecuteCommandResult { Success = false, Canceled = true };
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(ex, "Fetching production data failed");

                    return new ExecuteCommandResult { Success = false, Message = ex.Message };
                }
            },
            new CommandOptions
            {
                Description = "Copy the live production data directory down over SSH, redacted.",
                IconName = "CloudArrowDown",
                ConfirmationMessage =
                    $"This copies real customer data from {options.Host} onto this machine. " +
                    "It is redacted on the way in and deleted again after " +
                    $"{options.RetainDays} days. Continue?",
            });

        resource.WithCommand(
            "reset-user-data",
            "Reset to empty",
            context =>
            {
                if (Directory.Exists(path))
                {
                    Directory.Move(path, $"{path}.bak-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
                }
                Directory.CreateDirectory(path);
                RemoteUserData.PruneBackups(path, options.RetainDays, context.Logger);

                return Task.FromResult(new ExecuteCommandResult
                {
                    Success = true,
                    Message = "Data directory emptied; the previous contents are kept alongside it.",
                });
            },
            new CommandOptions
            {
                Description = "Start over with an empty data directory, keeping the current one.",
                IconName = "Eraser",
                ConfirmationMessage = "Empty the local data directory? The current contents are kept as a backup.",
            });

        resource.WithCommand(
            "restore-user-data",
            "Restore previous data",
            context =>
            {
                var backup = LatestBackup(path);
                if (backup is null)
                {
                    return Task.FromResult(new ExecuteCommandResult
                    {
                        Success = false,
                        Message = "No backup to restore.",
                    });
                }

                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                Directory.Move(backup, path);

                return Task.FromResult(new ExecuteCommandResult
                {
                    Success = true,
                    Message = $"Restored {Path.GetFileName(backup)}.",
                });
            },
            new CommandOptions
            {
                Description = "Put the most recent backup back in place.",
                IconName = "ArrowUndo",
                ConfirmationMessage = "Restore the most recent backup? The current data directory is deleted.",
            });

        if (WantsSyncOnStart(options.SyncOnStartSwitch))
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                // A test run points the app at its own throwaway directory. Filling that with a
                // copy of production would put real customer data into a tree tests delete, create
                // and share freely — and would do it on whatever machine happens to run CI.
                throw new InvalidOperationException(
                    $"{options.SyncOnStartSwitch} was passed, but '{options.LocalPathConfigurationKey}' " +
                    "points this run at an isolated data directory. Refusing to fetch production " +
                    "into a test directory: run without the isolation override, or without the switch.");
            }

            SyncBeforeStart(resource, options, path);
        }

        return resource;
    }

    /// <summary>
    /// Reads the switch off the process command line, and only there.
    /// <para>
    /// <see cref="Environment.GetCommandLineArgs"/> rather than
    /// <c>builder.Configuration</c> on purpose: configuration also binds environment variables and
    /// launch profiles, so reading it would let this be switched on permanently and invisibly by a
    /// line in <c>apphost.run.json</c>. Fetching production has to be typed out, every time.
    /// </para>
    /// <para>
    /// Reading the raw args also means an AppHost that rewrites its own argument list before
    /// building — normalising bare switches into <c>--flag=true</c>, as several do — needs no
    /// change here: the original <c>--sync-prod</c> is still on the command line.
    /// </para>
    /// </summary>
    private static bool WantsSyncOnStart(string @switch)
    {
        if (string.IsNullOrWhiteSpace(@switch)) return false;

        return Environment.GetCommandLineArgs().Any(arg =>
            arg.Equals(@switch, StringComparison.OrdinalIgnoreCase) ||
            arg.Equals($"{@switch}=true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fetches before the application model starts, so every resource comes up against the copy.
    /// <para>
    /// A failure here stops start-up. Someone who asked for production's state and silently got
    /// yesterday's — or the empty directory a first run leaves behind — would be testing the one
    /// thing they did not ask to test, and would have no way of telling.
    /// </para>
    /// </summary>
    private static void SyncBeforeStart<T>(
        IResourceBuilder<T> resource, UserDataSyncOptions options, string path)
        where T : class, IResource, IResourceWithEnvironment
    {
        resource.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
        {
            var logger = @event.Services
                .GetRequiredService<ResourceLoggerService>()
                .GetLogger(resource.Resource);

            // The dashboard is not up yet, so the resource log alone would be invisible for the
            // minute or two this takes. Say it on the console too.
            Console.WriteLine(
                $"[UserData] {options.SyncOnStartSwitch}: fetching production data from {options.Host} " +
                $"into {path} before anything starts. This is real customer data, redacted on the way in.");

            await RemoteUserData.FetchAsync(options, path, logger, ct).ConfigureAwait(false);

            Console.WriteLine("[UserData] production data in place; starting resources against it.");
        });
    }

    private static string? LatestBackup(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null || !Directory.Exists(parent)) return null;

        return Directory
            .EnumerateDirectories(parent, Path.GetFileName(Path.GetFullPath(path)) + ".bak-*")
            .OrderByDescending(d => d)
            .FirstOrDefault();
    }
}
