using System.Diagnostics;

namespace GAE.Narrator;

/// <summary>
/// Resolves a launchable Codex CLI process across direct executables and Windows npm shims.
/// ProcessStartInfo cannot execute a .cmd/.ps1 shim directly with shell execution disabled, so
/// Windows npm installations are launched through Node and the package's JavaScript entry point.
/// </summary>
public static class CodexCliLocator
{
    /// <summary>
    /// Creates a redirected, no-window process definition and adds any platform-specific bootstrap
    /// argument before the caller appends ordinary Codex CLI arguments.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string? configuredExecutable, string? workingDirectory = null)
    {
        var executable = string.IsNullOrWhiteSpace(configuredExecutable) ? "codex" : configuredExecutable.Trim();
        var nodeEntryPoint = ResolveWindowsNpmEntryPoint(executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = nodeEntryPoint is null ? executable : "node",
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (nodeEntryPoint is not null)
            startInfo.ArgumentList.Add(nodeEntryPoint);

        return startInfo;
    }

    private static string? ResolveWindowsNpmEntryPoint(string executable)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var executableName = Path.GetFileName(executable);
        var isNpmShim = string.Equals(executableName, "codex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(executableName, "codex.cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(executableName, "codex.ps1", StringComparison.OrdinalIgnoreCase);
        if (!isNpmShim)
            return null;

        string? npmDirectory;
        if (Path.IsPathRooted(executable))
        {
            npmDirectory = Path.GetDirectoryName(executable);
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            npmDirectory = string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "npm");
        }

        if (string.IsNullOrWhiteSpace(npmDirectory))
            return null;

        var candidate = Path.Combine(npmDirectory, "node_modules", "@openai", "codex", "bin", "codex.js");
        return File.Exists(candidate) ? candidate : null;
    }
}
