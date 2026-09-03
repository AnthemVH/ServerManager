using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ServerLauncher.Core.Updates;

/// <summary>
/// Registers the launcher to start when the current user logs in.
///
/// Writes only to the current user's Run key — no machine-wide or security settings are
/// touched, and it is applied solely when the user ticks the box in Settings. On a
/// dedicated box this is what brings game servers back after a reboot, paired with
/// Windows auto-logon.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ServerLauncher";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Adds or removes the login entry.</summary>
    /// <param name="enabled">Whether the launcher should start at login.</param>
    /// <param name="startMinimised">Pass the flag that starts it hidden in the tray.</param>
    /// <returns>True when the change was applied.</returns>
    public static bool SetEnabled(bool enabled, bool startMinimised)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var path = UpdateInstaller.CurrentExecutablePath;
            var command = startMinimised ? $"\"{path}\" --minimised" : $"\"{path}\"";
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                      or UnauthorizedAccessException
                                      or IOException
                                      or InvalidOperationException)
        {
            return false;
        }
    }
}
