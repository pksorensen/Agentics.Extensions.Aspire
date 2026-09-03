using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Aspire.Hosting.Stripe;

/// <summary>
/// Finding, installing and interrogating the Stripe CLI.
///
/// The reason this file exists: <c>AddExecutable("stripe-webhooks", "stripe", …)</c> against a
/// machine without the CLI dies with <c>exec: "stripe": executable file not found in $PATH</c>
/// — two red resources and nothing telling you what to do about it. Everything here is in
/// service of turning that into either a working binary or one actionable sentence.
/// </summary>
internal static class StripeCli
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private const string ReleasesBase = "https://github.com/stripe/stripe-cli/releases/download";

    internal const string InstallHint =
        "Stripe CLI not found. Install it from https://docs.stripe.com/stripe-cli#install " +
        "(or `brew install stripe/stripe-cli/stripe`), set STRIPE_CLI_BIN to its path, " +
        "or leave StripeListenOptions.InstallIfMissing on to have Aspire download it.";

    /// <summary>Binary name on this OS.</summary>
    internal static string BinaryName => OperatingSystem.IsWindows() ? "stripe.exe" : "stripe";

    /// <summary>
    /// Where a downloaded CLI lives. Versioned, so pinning a different version does not
    /// silently reuse the previous download. Deliberately not the system temp dir — in a
    /// devcontainer that is RAM.
    /// </summary>
    internal static string CacheDirectory(string version)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(root, "agentics", "stripe-cli", version);
    }

    /// <summary>
    /// Resolves the path the executable resources should run, without touching the network.
    /// Order: explicit option → <c>STRIPE_CLI_BIN</c> → an already-downloaded copy in the
    /// cache → PATH → the cache path the download will fill in before start.
    /// <para>
    /// <c>installNeeded</c> is true when nothing exists yet, which is the signal for
    /// <see cref="EnsureInstalledAsync"/> to actually fetch it.
    /// </para>
    /// </summary>
    internal static (string path, bool installNeeded) ResolvePath(StripeListenOptions options)
    {
        var explicitPath = options.CliPath ?? Environment.GetEnvironmentVariable("STRIPE_CLI_BIN");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return (explicitPath, false);
        }

        var cached = Path.Combine(CacheDirectory(options.CliVersion), BinaryName);
        if (File.Exists(cached))
        {
            return (cached, false);
        }

        // PATH before download: a CLI the developer installed themselves wins, so `stripe login`
        // state and their own version keep applying.
        if (IsOnPath())
        {
            return (BinaryName, false);
        }

        return (cached, true);
    }

    private static bool IsOnPath()
    {
        try
        {
            var psi = new ProcessStartInfo(BinaryName, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads the pinned CLI to <paramref name="targetPath"/> and verifies its sha256
    /// against the checksums file Stripe publishes beside the asset. A mismatch throws
    /// rather than running the binary.
    /// </summary>
    internal static async Task EnsureInstalledAsync(
        string targetPath, string version, CancellationToken ct)
    {
        if (File.Exists(targetPath)) return;

        var (assetName, checksumsName) = ResolveAsset(version);
        var assetUrl = $"{ReleasesBase}/v{version}/{assetName}";

        using var response = await s_http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        using (var sums = await s_http.GetAsync($"{ReleasesBase}/v{version}/{checksumsName}", ct)
            .ConfigureAwait(false))
        {
            if (sums.IsSuccessStatusCode)
            {
                var expected = ParseChecksum(
                    await sums.Content.ReadAsStringAsync(ct).ConfigureAwait(false), assetName);
                if (expected is not null)
                {
                    var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Checksum mismatch for {assetName}: expected {expected}, got {actual}.");
                    }
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        ExtractBinary(bytes, assetName, targetPath);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// Maps the current runtime onto Stripe's release asset names. They are not uniform:
    /// linux and windows use <c>x86_64</c>, macOS builds are tagged <c>mac-os</c>, and the
    /// checksum manifest is per-OS rather than per-release.
    /// </summary>
    private static (string asset, string checksums) ResolveAsset(string version)
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "arm64",
            var other => throw new PlatformNotSupportedException(
                $"No published Stripe CLI build for architecture '{other}'. {InstallHint}"),
        };

        if (OperatingSystem.IsWindows())
        {
            // Stripe publishes no arm64 Windows build; the x86_64 zip runs under emulation.
            return ($"stripe_{version}_windows_x86_64.zip", "stripe-windows-checksums.txt");
        }

        if (OperatingSystem.IsMacOS())
        {
            return ($"stripe_{version}_mac-os_{arch}.tar.gz", "stripe-mac-checksums.txt");
        }

        if (OperatingSystem.IsLinux())
        {
            return ($"stripe_{version}_linux_{arch}.tar.gz", "stripe-linux-checksums.txt");
        }

        throw new PlatformNotSupportedException($"Unsupported OS for the Stripe CLI. {InstallHint}");
    }

    private static void ExtractBinary(byte[] archive, string assetName, string targetPath)
    {
        using var buffer = new MemoryStream(archive, writable: false);

        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), BinaryName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{BinaryName}' not found inside {assetName}.");
            entry.ExtractToFile(targetPath, overwrite: true);

            return;
        }

        using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (!string.Equals(Path.GetFileName(entry.Name), BinaryName, StringComparison.Ordinal))
            {
                continue;
            }

            entry.ExtractToFile(targetPath, overwrite: true);

            return;
        }

        throw new InvalidOperationException($"'{BinaryName}' not found inside {assetName}.");
    }

    private static string? ParseChecksum(string text, string asset)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1] == asset)
            {
                return parts[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Asks the CLI for the webhook signing secret with <c>stripe listen --print-secret</c>,
    /// which prints it and exits. The key goes in via <c>STRIPE_API_KEY</c> rather than
    /// <c>--api-key</c> so a live secret key never lands in a process listing or a log line.
    /// Returns null on any failure — a missing secret is a warning, not a dead AppHost.
    /// </summary>
    internal static async Task<string?> TryGetWebhookSecretAsync(
        string cliPath, string apiKey, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(cliPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("listen");
            psi.ArgumentList.Add("--print-secret");
            psi.Environment["STRIPE_API_KEY"] = apiKey;

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("whsec_", StringComparison.Ordinal))
                {
                    return trimmed;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
