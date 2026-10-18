using Microsoft.Win32;

namespace FileTabs;

/// <summary>
/// Per-user autostart through HKCU Run. Kept separate from AppSettings so
/// the registry remains the source of truth and moving the exe is detectable.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FileTabs";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not string { Length: > 0 } command) return false;

        // A stale value from an older install location should not read as
        // "on": re-enabling rewrites it to the current exe.
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        return string.Equals(command.Trim().Trim('"'), exe, StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
