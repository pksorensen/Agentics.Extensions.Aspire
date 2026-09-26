using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.UserData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        where T : class, IResource, IResourceWithEnvironment, IResourceWithWaitSupport
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
                    await RemoteUserData.FetchAsync(
                            options,
                            path,
                            context.Logger,
                            context.Services.GetRequiredService<IInteractionService>(),
                            context.CancellationToken)
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
                Description = "Copy the live production data directory to this machine, redacted.",
                IconName = "CloudArrowDown",
                ConfirmationMessage =
                    $"This copies real customer data from {options.SourceDisplayName} onto this machine. " +
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

        var syncOnStart = ReadSyncOnStart(options.SyncOnStartSwitch);
        if (syncOnStart.Enabled)
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

            options.AzureUsername = syncOnStart.Username;
            AddSyncResource(resource, options, path);
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
    private static (bool Enabled, string? Username) ReadSyncOnStart(string @switch)
    {
        if (string.IsNullOrWhiteSpace(@switch)) return (false, null);

        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.Equals($"{@switch}=true", StringComparison.OrdinalIgnoreCase))
                return (true, null);

            var prefix = @switch + "=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[prefix.Length..].Trim();
                if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return (false, null);
                return (true, value.Equals("true", StringComparison.OrdinalIgnoreCase) ? null : value);
            }

            if (!arg.Equals(@switch, StringComparison.OrdinalIgnoreCase)) continue;

            var username = index + 1 < args.Length && !args[index + 1].StartsWith('-')
                ? args[index + 1].Trim()
                : null;
            return (true, string.IsNullOrWhiteSpace(username) ? null : username);
        }

        return (false, null);
    }

    /// <summary>
    /// Models the fetch as a one-shot resource instead of blocking <c>BeforeStartEvent</c>. This
    /// lets the dashboard connect immediately and show progress while the application resource
    /// waits for a successful, atomic snapshot install.
    /// </summary>
    private static void AddSyncResource<T>(
        IResourceBuilder<T> resource, UserDataSyncOptions options, string path)
        where T : class, IResource, IResourceWithEnvironment, IResourceWithWaitSupport
    {
        var sync = resource.ApplicationBuilder
            .AddResource(new UserDataSyncResource($"{resource.Resource.Name}-sync-prod"))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Production data sync",
                State = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Info),
                Properties = [
                    new("source", options.SourceDisplayName),
                    new("destination", path),
                ],
                IconName = "CloudArrowDown",
            });

        sync.OnInitializeResource((syncResource, @event, ct) =>
        {
            // The initialize-event token belongs to this callback and is cancelled when the
            // callback returns. The sync is a background resource, so its lifetime follows the
            // AppHost instead.
            var stopping = @event.Services
                .GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping;
            _ = RunSyncResourceAsync(syncResource, @event, options, path, stopping);
            return Task.CompletedTask;
        });

        resource.WaitForCompletion(sync);
    }

    private static async Task RunSyncResourceAsync(
        UserDataSyncResource resource,
        InitializeResourceEvent @event,
        UserDataSyncOptions options,
        string path,
        CancellationToken ct)
    {
        await @event.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            State = new ResourceStateSnapshot("Downloading", KnownResourceStateStyles.Info),
            StartTimeStamp = DateTime.UtcNow,
            Properties = [
                new("source", options.SourceDisplayName),
                new("destination", path),
            ],
        }).ConfigureAwait(false);

        try
        {
            await RemoteUserData.FetchAsync(
                    options,
                    path,
                    @event.Logger,
                    @event.Services.GetRequiredService<IInteractionService>(),
                    ct)
                .ConfigureAwait(false);

            await @event.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Finished, KnownResourceStateStyles.Success),
                ExitCode = 0,
                StopTimeStamp = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await @event.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Exited, KnownResourceStateStyles.Warn),
                ExitCode = 130,
                StopTimeStamp = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            @event.Logger.LogError(ex, "Production data sync failed");
            Console.Error.WriteLine($"Production data sync failed: {ex}");
            await @event.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Exited, KnownResourceStateStyles.Error),
                ExitCode = 1,
                StopTimeStamp = DateTime.UtcNow,
                Properties = [.. snapshot.Properties, new("error", ex.Message)],
            }).ConfigureAwait(false);
        }
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
