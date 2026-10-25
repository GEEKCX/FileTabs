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
    /// <summary>写进 Run 值的参数：登录启动时在后台静默起来。</summary>
    public const string SilentArgument = "--silent";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not string { Length: > 0 } command) return false;

        // A stale value from an older install location should not read as
        // "on": re-enabling rewrites it to the current exe.
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        return string.Equals(StripArguments(command), exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 静默启动之前写下的旧值：恰好是 "<exe>" 或 exe，没有任何额外参数。
    /// 用户自己加过参数、或指向别的程序的值都不算旧值，绝不改写。
    /// </summary>
    public static bool IsLegacyCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not string { Length: > 0 } command) return false;
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var trimmed = command.Trim();
        return string.Equals(trimmed, $"\"{exe}\"", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取出 Run 值里的 exe 路径，忽略引号和后面的参数。</summary>
    private static string StripArguments(string command)
    {
        var trimmed = command.Trim();
        // CommandLineToArgvW 的规则：带引号的路径在收尾引号处结束，
        // 不带引号的在第一个空格处结束。
        var closingQuote = trimmed.StartsWith('"') ? trimmed.IndexOf('"', 1) : -1;
        var path = closingQuote > 0 ? trimmed[1..closingQuote] : trimmed.Split(' ')[0];
        return path.Trim().Trim('"');
    }

    public static void Enable()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        key.SetValue(ValueName, $"\"{exe}\" {SilentArgument}", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
