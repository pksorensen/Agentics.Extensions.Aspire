using System.Diagnostics;
using System.Text;

namespace Aspire.Hosting;

/// <summary>How a single image probe came out.</summary>
public enum RegistryAccessState
{
    /// <summary>The image manifest was readable — this machine can pull it.</summary>
    Reachable,

    /// <summary>The registry refused the credentials, or there were none.</summary>
    Unauthorized,

    /// <summary>
    /// Neither: the daemon was missing, the network was down, the probe timed
    /// out, or the image genuinely does not exist. Never reported as a
    /// sign-in problem, because it is not one.
    /// </summary>
    Unknown,
}

/// <summary>The outcome of probing one image reference.</summary>
/// <param name="Image">The image reference that was probed.</param>
/// <param name="State">What the probe concluded.</param>
/// <param name="Detail">The runtime's own message, trimmed to one line.</param>
public sealed record RegistryAccessResult(string Image, RegistryAccessState State, string Detail);

/// <summary>
/// Asks the container runtime whether it can read an image's manifest.
/// </summary>
/// <remarks>
/// <para>
/// <c>docker manifest inspect</c> is the probe because it exercises docker's
/// real credential chain — <c>config.json</c>, <c>credHelpers</c>, the
/// credential store — and stops at the manifest, so it costs one request and
/// downloads no layers. Reading a config file instead would answer a different,
/// less useful question: docker's own resolution order is what actually decides
/// whether the pull works.
/// </para>
/// <para>
/// Only two outcomes are actionable. "Unauthorized" is the one worth
/// interrupting someone about; everything else is <see cref="RegistryAccessState.Unknown"/>,
/// because an AppHost that shouts "sign in!" at someone whose wifi dropped is
/// worse than one that says nothing.
/// </para>
/// </remarks>
public static class RegistryAccessProbe
{
    /// <summary>Probes one image reference.</summary>
    public static async Task<RegistryAccessResult> ProbeAsync(
        string image,
        string containerRuntime,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRuntime);

        var startInfo = new ProcessStartInfo(containerRuntime)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("manifest");
        startInfo.ArgumentList.Add("inspect");
        startInfo.ArgumentList.Add(image);

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        try
        {
            if (!process.Start())
            {
                return new RegistryAccessResult(image, RegistryAccessState.Unknown, $"could not start {containerRuntime}");
            }
        }
        catch (Exception error)
        {
            return new RegistryAccessResult(image, RegistryAccessState.Unknown, error.Message);
        }

        process.BeginErrorReadLine();
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new RegistryAccessResult(image, RegistryAccessState.Unknown, "the probe timed out");
        }

        var detail = FirstLine(stderr.ToString());
        if (process.ExitCode == 0)
        {
            return new RegistryAccessResult(image, RegistryAccessState.Reachable, detail);
        }
        return new RegistryAccessResult(image, Classify(detail), detail);
    }

    private static RegistryAccessState Classify(string detail)
    {
        // Docker reports the registry's own 401 verbatim, and the wording
        // varies between "unauthorized", "authentication required" and the
        // familiar "may require 'docker login'" — match on all three rather
        // than on one exact sentence.
        if (detail.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("docker login", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return RegistryAccessState.Unauthorized;
        }
        return RegistryAccessState.Unknown;
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return string.Empty;
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        return newline < 0 ? trimmed : trimmed[..newline];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The probe is advisory; a process that already went away, or one
            // we may not signal, must not take the AppHost down with it.
        }
    }
}
