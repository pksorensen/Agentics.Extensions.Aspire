using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Terminal;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

public static class TerminalAppExtensions
{
    /// <summary>
    /// Adds a terminal application served in the browser through ttyd + xterm.js. The ttyd
    /// process is managed by Aspire (start/stop/restart, logs, health) and a clickable URL
    /// appears in the dashboard.
    /// <para>
    /// The binary is resolved in order: (1) build from local Go source when
    /// <paramref name="projectDir"/> is a real directory and Go is available; (2) an existing
    /// prebuilt binary at the target path; (3) download a prebuilt CLI from the agentics.dk
    /// install store when <paramref name="agenticsComponent"/> is set. Supply at least one of
    /// <paramref name="projectDir"/> or <paramref name="agenticsComponent"/>.
    /// </para>
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name shown in the Aspire dashboard.</param>
    /// <param name="projectDir">Path to the Go project directory (built from source when present). Optional.</param>
    /// <param name="entryPoint">Go entry point relative to projectDir (default: "./main.go").</param>
    /// <param name="outputBinary">Output binary name (default: derived from name).</param>
    /// <param name="agenticsComponent">Install-store component id (e.g. "vibecast") to download when no source build is possible.</param>
    /// <param name="agenticsVersion">Pin a specific published version; defaults to the latest published release.</param>
    /// <param name="agenticsBaseUrl">Override the install-store host (default: AGENTICS_BASE_URL env, else https://agentics.dk).</param>
    public static IResourceBuilder<TerminalAppResource> AddTerminalApp(
        this IDistributedApplicationBuilder builder,
        string name,
        string? projectDir = null,
        string entryPoint = "./main.go",
        string? outputBinary = null,
        string? agenticsComponent = null,
        string? agenticsVersion = null,
        string? agenticsBaseUrl = null)
    {
        if (projectDir is null && agenticsComponent is null)
        {
            throw new ArgumentException(
                $"AddTerminalApp(\"{name}\", …) needs either a projectDir (to build from Go source) " +
                "or an agenticsComponent (to download a prebuilt CLI from the agentics.dk install store).");
        }

        var ttydCommand = FindTtyd()
            ?? throw new InvalidOperationException(
                "ttyd not found. Install from https://github.com/tsl0922/ttyd or via your package manager.");

        var ttydPort = GetFreePort();
        var viewerHtmlPath = ExtractViewerHtml();

        var binaryName = outputBinary ?? name;
        if (OperatingSystem.IsWindows()) binaryName += ".exe";

        // Where the binary lives / runs from. With a projectDir it sits in the source tree
        // (as before); a pure download consumer gets a stable per-resource cache dir.
        var fullProjectDir = projectDir is not null
            ? Path.GetFullPath(projectDir)
            : DefaultBinaryCacheDir(name);
        var fullBinaryPath = Path.Combine(fullProjectDir, binaryName);

        var resource = new TerminalAppResource(name, ttydCommand, fullProjectDir)
        {
            BinaryPath = fullBinaryPath,
        };

        var resourceBuilder = builder.AddResource(resource)
            .WithArgs(
                "--port", ttydPort.ToString(),
                "--index", viewerHtmlPath,
                "--writable",
                fullBinaryPath)
            .WithHttpEndpoint(port: ttydPort, name: "ttyd", isProxied: false);

        // Resolve the binary before ttyd starts. Resolution order:
        //   1. Build from local Go source  (projectDir is a real dir + Go present)
        //   2. Reuse an existing binary at fullBinaryPath
        //   3. Download a prebuilt CLI from the agentics.dk install store
        builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
        {
            var notificationService = builder.Services
                .BuildServiceProvider()
                .GetRequiredService<ResourceNotificationService>();

            await notificationService.PublishUpdateAsync(resource, s => s with
            {
                State = new ResourceStateSnapshot("Starting", KnownResourceStateStyles.Info),
                Properties = [new("project_dir", fullProjectDir)],
                StartTimeStamp = DateTime.UtcNow,
            });

            var goCommand = FindGo();
            var hasSource = projectDir is not null
                && Directory.Exists(fullProjectDir)
                && File.Exists(Path.Combine(fullProjectDir, entryPoint.Replace("./", string.Empty)));
            var canBuildFromSource = hasSource && goCommand is not null;

            // Path 1 — build from Go source (unchanged dev behavior).
            if (canBuildFromSource)
            {
                await notificationService.PublishUpdateAsync(resource, s => s with
                {
                    State = new ResourceStateSnapshot("Building", KnownResourceStateStyles.Info),
                    Properties = [
                        new("project_dir", fullProjectDir),
                        new("go", goCommand!),
                    ],
                });

                var psi = new ProcessStartInfo(goCommand!, $"build -o {binaryName} {entryPoint}")
                {
                    WorkingDirectory = fullProjectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                var proc = Process.Start(psi);
                if (proc is null)
                {
                    await notificationService.PublishUpdateAsync(resource, s => s with
                    {
                        State = new ResourceStateSnapshot("FailedToStart", KnownResourceStateStyles.Error),
                        StopTimeStamp = DateTime.UtcNow,
                    });
                    throw new InvalidOperationException("Failed to start go build process.");
                }

                await proc.WaitForExitAsync(ct);
                var stderr = await proc.StandardError.ReadToEndAsync(ct);

                if (proc.ExitCode != 0)
                {
                    await notificationService.PublishUpdateAsync(resource, s => s with
                    {
                        State = new ResourceStateSnapshot("BuildFailed", KnownResourceStateStyles.Error),
                        ExitCode = proc.ExitCode,
                        StopTimeStamp = DateTime.UtcNow,
                    });
                    throw new InvalidOperationException($"Go build failed (exit {proc.ExitCode}): {stderr}");
                }

                await notificationService.PublishUpdateAsync(resource, s => s with
                {
                    State = new ResourceStateSnapshot("Built", KnownResourceStateStyles.Success),
                    Properties = [
                        new("project_dir", fullProjectDir),
                        new("go", goCommand!),
                        new("binary", fullBinaryPath),
                        new("ttyd_port", ttydPort.ToString()),
                    ],
                });
                return;
            }

            // Path 2 — reuse an existing binary (e.g. previously downloaded or hand-placed).
            if (File.Exists(fullBinaryPath))
            {
                await notificationService.PublishUpdateAsync(resource, s => s with
                {
                    State = new ResourceStateSnapshot("Built", KnownResourceStateStyles.Success),
                    Properties = [
                        new("project_dir", fullProjectDir),
                        new("binary", fullBinaryPath),
                        new("source", hasSource ? "existing binary (Go not found)" : "existing binary"),
                        new("ttyd_port", ttydPort.ToString()),
                    ],
                });
                return;
            }

            // Path 3 — download a prebuilt CLI from the agentics.dk install store.
            if (agenticsComponent is not null)
            {
                var baseUrl = (agenticsBaseUrl
                        ?? Environment.GetEnvironmentVariable("AGENTICS_BASE_URL")
                        ?? "https://agentics.dk")
                    .TrimEnd('/');

                await notificationService.PublishUpdateAsync(resource, s => s with
                {
                    State = new ResourceStateSnapshot("Downloading", KnownResourceStateStyles.Info),
                    Properties = [
                        new("component", agenticsComponent),
                        new("source", baseUrl),
                    ],
                });

                try
                {
                    var resolvedVersion = await DownloadFromAgenticsAsync(
                        agenticsComponent, agenticsVersion, baseUrl, fullBinaryPath, ct);

                    await notificationService.PublishUpdateAsync(resource, s => s with
                    {
                        State = new ResourceStateSnapshot("Built", KnownResourceStateStyles.Success),
                        Properties = [
                            new("project_dir", fullProjectDir),
                            new("binary", fullBinaryPath),
                            new("component", agenticsComponent),
                            new("version", resolvedVersion),
                            new("source", baseUrl),
                            new("ttyd_port", ttydPort.ToString()),
                        ],
                    });
                }
                catch (Exception ex)
                {
                    await notificationService.PublishUpdateAsync(resource, s => s with
                    {
                        State = new ResourceStateSnapshot("FailedToStart", KnownResourceStateStyles.Error),
                        Properties = [
                            new("component", agenticsComponent),
                            new("source", baseUrl),
                            new("error", ex.Message),
                        ],
                        ExitCode = 1,
                        StopTimeStamp = DateTime.UtcNow,
                    });
                    // Don't throw — let the rest of the app start; the resource shows as failed.
                }
                return;
            }

            // No source build, no existing binary, no download component.
            await notificationService.PublishUpdateAsync(resource, s => s with
            {
                State = new ResourceStateSnapshot("FailedToStart", KnownResourceStateStyles.Error),
                Properties = [
                    new("project_dir", fullProjectDir),
                    new("error", "No Go source to build, no existing binary, and no agenticsComponent to download."),
                    new("binary_path", fullBinaryPath),
                ],
                ExitCode = 1,
                StopTimeStamp = DateTime.UtcNow,
            });
        });

        return resourceBuilder;
    }

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// Downloads a prebuilt CLI from the agentics.dk install store, verifies its sha256
    /// checksum, writes it to <paramref name="targetPath"/> (+x on unix), and returns the
    /// resolved semantic version.
    /// </summary>
    private static async Task<string> DownloadFromAgenticsAsync(
        string component, string? version, string baseUrl, string targetPath, CancellationToken ct)
    {
        var (os, arch) = ResolvePlatform();

        // Resolve version (latest published release when not pinned).
        var semver = version;
        if (string.IsNullOrWhiteSpace(semver))
        {
            using var latest = await s_http.GetAsync(
                $"{baseUrl}/api/releases/latest?component={Uri.EscapeDataString(component)}", ct);
            latest.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await latest.Content.ReadAsStringAsync(ct));
            semver = doc.RootElement.TryGetProperty("semver", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(semver))
            {
                throw new InvalidOperationException(
                    $"Could not resolve latest version for '{component}' from {baseUrl}.");
            }
        }

        var assetName = $"{component}_{os}_{arch}" + (os == "windows" ? ".exe" : string.Empty);

        // Download the binary.
        var downloadUrl =
            $"{baseUrl}/install/{Uri.EscapeDataString(component)}/download" +
            $"?os={os}&arch={arch}&version={Uri.EscapeDataString(semver!)}";
        using var binResp = await s_http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        binResp.EnsureSuccessStatusCode();
        var bytes = await binResp.Content.ReadAsByteArrayAsync(ct);

        // Fetch + verify the sha256 checksum for this asset.
        var checksumsUrl =
            $"{baseUrl}/api/releases/{Uri.EscapeDataString($"{component}-{semver}")}" +
            $"/attachments/{Uri.EscapeDataString($"{component}_{semver}_checksums.txt")}";
        using var sumResp = await s_http.GetAsync(checksumsUrl, ct);
        if (sumResp.IsSuccessStatusCode)
        {
            var checksumsText = await sumResp.Content.ReadAsStringAsync(ct);
            var expected = ParseChecksum(checksumsText, assetName);
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

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllBytesAsync(targetPath, bytes, ct);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return semver!;
    }

    /// <summary>Maps the current runtime to install-store os/arch tokens.</summary>
    private static (string os, string arch) ResolvePlatform()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsLinux() ? "linux"
            : throw new PlatformNotSupportedException("Unsupported OS for the agentics install store.");

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            var other => throw new PlatformNotSupportedException(
                $"Unsupported architecture '{other}' for the agentics install store."),
        };

        return (os, arch);
    }

    /// <summary>Finds the sha256 for <paramref name="asset"/> in a sha256sum-format file.</summary>
    private static string? ParseChecksum(string text, string asset)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1] == asset)
            {
                return parts[0];
            }
        }
        return null;
    }

    /// <summary>Stable per-resource cache dir for downloaded binaries.</summary>
    private static string DefaultBinaryCacheDir(string name)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        var dir = Path.Combine(root, "agentics", "terminal", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string? FindGo()
    {
        // Check PATH first
        try
        {
            var psi = new ProcessStartInfo("go", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0) return "go";
            }
        }
        catch
        {
            // not on PATH
        }

        // Check common locations
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "go", "bin", "go"),
            "/usr/local/go/bin/go",
            "/usr/lib/go/bin/go",
            "/snap/bin/go",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? FindTtyd()
    {
        // Check PATH first
        try
        {
            var psi = new ProcessStartInfo("ttyd", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0) return "ttyd";
            }
        }
        catch
        {
            // not on PATH
        }

        // Check common locations
        var candidates = new[]
        {
            "/usr/local/bin/ttyd",
            "/usr/bin/ttyd",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", "ttyd"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ExtractViewerHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("viewer.html"))
            ?? throw new InvalidOperationException("Embedded viewer.html not found in assembly.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"aspire-terminal-viewer-{Guid.NewGuid():N}.html");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var file = File.Create(tempPath);
        stream.CopyTo(file);

        return tempPath;
    }
}
