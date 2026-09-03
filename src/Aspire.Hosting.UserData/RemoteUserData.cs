using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.UserData;

/// <summary>
/// The fetch itself: resolve the remote directory, stream it down, redact it, mark it.
/// </summary>
internal static class RemoteUserData
{
    /// <summary>
    /// Written into every fetched directory. It is not documentation — the application reads it
    /// and refuses to do anything that would reach real users (sending mail being the obvious
    /// one). Keep the name and the <c>source</c> value stable; apps match on them.
    /// </summary>
    internal const string MarkerFileName = ".snapshot-origin.json";

    internal static async Task FetchAsync(
        UserDataSyncOptions options, string localPath, ILogger logger, CancellationToken ct)
    {
        var host = options.Host
            ?? throw new InvalidOperationException("No host configured — call FromCoolify or FromPath.");

        var remotePath = options.RemotePath;
        if (string.IsNullOrEmpty(remotePath))
        {
            logger.LogInformation("Resolving the data directory for {Fqdn} on {Host}", options.CoolifyFqdn, host);
            remotePath = await ResolveCoolifyPathAsync(host, options, ct).ConfigureAwait(false);
            logger.LogInformation("Found {RemotePath}", remotePath);
        }

        var staging = localPath + ".incoming";
        // A previous run that died halfway leaves this behind; starting from its remains would
        // mix two copies of production, which is worse than starting over.
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        try
        {
            logger.LogInformation("Copying {Host}:{RemotePath}", host, remotePath);
            await StreamDirectoryAsync(host, remotePath, staging, options.Excludes, ct).ConfigureAwait(false);

            var redacted = Redact(staging, options.Redactions, logger);
            logger.LogInformation("Redacted {Count} file(s)", redacted);

            await WriteMarkerAsync(staging, host, remotePath, options, ct).ConfigureAwait(false);

            Install(staging, localPath, options.RetainDays, logger);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
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

        await ssh.StandardOutput.BaseStream.CopyToAsync(tar.StandardInput.BaseStream, ct).ConfigureAwait(false);
        tar.StandardInput.Close();

        await Task.WhenAll(ssh.WaitForExitAsync(ct), tar.WaitForExitAsync(ct)).ConfigureAwait(false);

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

                    File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
        string root, string host, string remotePath, UserDataSyncOptions options, CancellationToken ct)
    {
        var marker = new JsonObject
        {
            ["source"] = "prod",
            ["host"] = host,
            ["remotePath"] = remotePath,
            ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["excluded"] = new JsonArray([.. options.Excludes.Select(e => (JsonNode)JsonValue.Create(e)!)]),
            ["redactions"] = new JsonArray([.. options.Redactions.Select(r => (JsonNode)JsonValue.Create(r.PathGlob)!)]),
            ["retainDays"] = options.RetainDays,
        };

        await File.WriteAllTextAsync(
            Path.Combine(root, MarkerFileName),
            marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
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
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return (process.ExitCode, await stdout, await stderr);
    }
}
