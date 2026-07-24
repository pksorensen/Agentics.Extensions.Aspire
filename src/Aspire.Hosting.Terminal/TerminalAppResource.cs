using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Terminal;

/// <summary>
/// An Aspire resource representing a terminal application served via ttyd.
/// Extends <see cref="ExecutableResource"/> so Aspire manages the ttyd process lifecycle,
/// providing start/stop/restart, dashboard logs, and health monitoring for free.
/// </summary>
public class TerminalAppResource(string name, string command, string workingDirectory)
    : ExecutableResource(name, command, workingDirectory)
{
    /// <summary>
    /// The path to the binary that ttyd will execute.
    /// </summary>
    public string BinaryPath { get; set; } = string.Empty;
}
