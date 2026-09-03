using System.Diagnostics;
using System.Text;
using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Processes;

/// <summary>
/// Translates a <see cref="ServerDefinition"/> into a <see cref="ProcessStartInfo"/>.
/// Kept as a pure function so the launch matrix and path quoting can be verified
/// without actually starting processes.
/// </summary>
public static class ScriptLauncher
{
    public static bool IsSupportedScript(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    public static ProcessStartInfo BuildStartInfo(ServerDefinition definition, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(definition.ScriptPath))
        {
            throw new InvalidOperationException($"Server '{definition.Name}' has no script path set.");
        }

        var scriptPath = Path.GetFullPath(definition.ScriptPath);
        var extraArgs = definition.Arguments?.Trim() ?? string.Empty;
        var extension = Path.GetExtension(scriptPath);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = definition.ResolveWorkingDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";

            // cmd.exe strips the outermost quote pair before parsing, so a path
            // containing spaces needs the whole command wrapped a second time.
            var inner = Quote(scriptPath);
            if (extraArgs.Length > 0)
            {
                inner += " " + extraArgs;
            }

            startInfo.Arguments = $"/c \"{inner}\"";
        }
        else if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = string.IsNullOrWhiteSpace(settings.PowerShellPath)
                ? "powershell.exe"
                : settings.PowerShellPath;

            // -NoProfile keeps a user's profile from changing behaviour; -ExecutionPolicy
            // Bypass applies to this process only and never changes machine policy.
            var args = $"-NoProfile -ExecutionPolicy Bypass -File {Quote(scriptPath)}";
            if (extraArgs.Length > 0)
            {
                args += " " + extraArgs;
            }

            startInfo.Arguments = args;
        }
        else
        {
            startInfo.FileName = scriptPath;
            startInfo.Arguments = extraArgs;
        }

        foreach (var (key, value) in definition.EnvironmentVariables)
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('\t') ? $"\"{value}\"" : value;
}
