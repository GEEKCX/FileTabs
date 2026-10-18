using Microsoft.Win32;
using System.IO;

namespace FileTabs;

/// <summary>
/// Phase 2 takeover: registry redirect so "open folder" verbs launch FileTabs
/// instead of Explorer. Everything is per-user (HKCU\Software\Classes), no
/// admin rights needed.
///
/// Windows resolves folder opening through three classes: Directory for
/// filesystem folders, Drive for drive roots, Folder as the base class. The
/// stock handler is a COM object referenced by a "DelegateExecute" GUID.
///
/// Where that GUID lives matters (verified on this machine): it sits in the
/// verb's *command* subkey, not on the verb key itself —
/// HKCU\Software\Classes\Folder\shell\open\command has DelegateExecute =
/// {11dbb47c-a525-400b-9e80-a54615a090c0} plus a fallback command line. As
/// long as that value is non-empty the shell uses the COM handler and ignores
/// our command line, so we overwrite it with an empty string, which makes the
/// shell fall back to the default value we wrote.
///
/// We do not remove anything: values are overwritten and the original state
/// (including "this key/value did not exist") is saved under
/// HKCU\Software\FileTabs\NativeRedirectBackup. Uninstall restores it exactly.
/// </summary>
public static class NativeRedirect
{
    /// <summary>Verb keys that receive "&lt;exe&gt; \"%1\"" (folder path argument).</summary>
    private static readonly string[] FolderVerbs =
    {
        @"Software\Classes\Directory\shell\open",
        @"Software\Classes\Directory\shell\opennewwindow",
        @"Software\Classes\Drive\shell\open",
        @"Software\Classes\Drive\shell\opennewwindow",
        @"Software\Classes\Folder\shell\open",
        @"Software\Classes\Folder\shell\opennewwindow",
    };

    /// <summary>Win+E's OpenNewWindow verb; gets "&lt;exe&gt;" with no argument.</summary>
    private const string WinEVerb =
        @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\OpenNewWindow";

    private const string BackupRoot = @"Software\FileTabs\NativeRedirectBackup";

    public static bool IsInstalled()
    {
        using var command = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Directory\shell\open\command");
        if (command?.GetValue(null) as string is not { } cmd ||
            !cmd.Contains("FileTabs.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // A non-empty DelegateExecute means the shell still hands off to the
        // Explorer COM handler, so the redirect would not actually work.
        return command.GetValue("DelegateExecute") is not string { Length: > 0 };
    }

    public static void Install()
    {
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe))
        {
            AppStorage.Log("NativeRedirect.Install: ProcessPath unavailable, aborting.");
            return;
        }

        foreach (var verb in FolderVerbs)
        {
            PatchVerb(verb, $"\"{exe}\" \"%1\"");
        }

        // Win+E has no path argument.
        PatchVerb(WinEVerb, $"\"{exe}\"");
        AppStorage.Log("NativeRedirect installed.");
    }

    public static void Uninstall()
    {
        foreach (var verb in FolderVerbs)
        {
            UnpatchVerb(verb);
        }
        UnpatchVerb(WinEVerb);

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(BackupRoot, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppStorage.Log($"NativeRedirect: backup cleanup failed: {ex.Message}");
        }
        AppStorage.Log("NativeRedirect uninstalled.");
    }

    private static void PatchVerb(string verbKeyPath, string commandLine)
    {
        string commandKeyPath = $@"{verbKeyPath}\command";
        BackupValue(commandKeyPath, null);
        BackupValue(commandKeyPath, "DelegateExecute");
        // Slot written by the first iteration of this feature, which put the
        // value on the verb key where the shell never reads it.
        BackupValue(verbKeyPath, "DelegateExecute");

        using var command = Registry.CurrentUser.CreateSubKey(commandKeyPath);
        command.SetValue(null, commandLine, RegistryValueKind.String);
        // An empty DelegateExecute disables the COM handler; without it the
        // shell ignores our command line and keeps launching Explorer.
        command.SetValue("DelegateExecute", string.Empty, RegistryValueKind.String);

        // Drop the useless verb-level leftover so installs converge on the
        // shape Uninstall restores from.
        using var verb = Registry.CurrentUser.OpenSubKey(verbKeyPath, writable: true);
        if (verb?.GetValue("DelegateExecute") is string { Length: 0 })
        {
            verb.DeleteValue("DelegateExecute", throwOnMissingValue: false);
        }
    }

    /// <summary>Runs in reverse order of PatchVerb: deepest keys first, so the
    /// verb key can be deleted again once its command subkey is gone.</summary>
    private static void UnpatchVerb(string verbKeyPath)
    {
        bool touched = RestoreValue($@"{verbKeyPath}\command", null);
        touched |= RestoreValue($@"{verbKeyPath}\command", "DelegateExecute");
        touched |= RestoreValue(verbKeyPath, "DelegateExecute");
        // Only trim when we actually had backups, so a spurious uninstall on a
        // clean machine never deletes anything.
        if (touched) TrimEmptyKeys($@"{verbKeyPath}\command");
    }

    /// <summary>
    /// Store the current value, or record that the key/value did not exist,
    /// so Uninstall can restore the exact prior state.
    /// </summary>
    private static void BackupValue(string keyPath, string? valueName)
    {
        string slot = SlotName(keyPath, valueName);
        // Already backed up by an earlier Install call - keep the original.
        using (var existing = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{slot}"))
        {
            if (existing is not null) return;
        }
        using var backup = Registry.CurrentUser.CreateSubKey($@"{BackupRoot}\{slot}");
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key is null)
        {
            backup.SetValue("state", "no-key");
            return;
        }
        var value = key.GetValue(valueName);
        if (value is null)
        {
            backup.SetValue("state", "no-value");
            return;
        }
        backup.SetValue("state", "value");
        backup.SetValue("data", value, key.GetValueKind(valueName!));
    }

    /// <summary>Restores one backed-up value; false when nothing was ever
    /// backed up for it.</summary>
    private static bool RestoreValue(string keyPath, string? valueName)
    {
        string slot = SlotName(keyPath, valueName);
        using var backup = Registry.CurrentUser.OpenSubKey($@"{BackupRoot}\{slot}");
        if (backup is null) return false;
        string state = backup.GetValue("state") as string ?? "no-key";

        // Drop what we wrote first, whatever the prior state was; the "value"
        // branch below puts the original back.
        ClearValue(keyPath, valueName);

        switch (state)
        {
            case "value":
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
                {
                    var data = backup.GetValue("data");
                    if (key is null || data is null) return true;
                    RegistryValueKind kind = RegistryValueKind.String;
                    try { kind = backup.GetValueKind("data"); } catch (IOException) { }
                    key.SetValue(valueName, data, kind);
                }
                return true;
            case "no-key":
                // Nothing existed before us; drop the key we may have created
                // (only when empty, to never delete unrelated values).
                TryDeleteEmptyKey(keyPath);
                return true;
        }
        return true;
    }

    /// <summary>Deletes a value, tolerating a missing key or value and the
    /// unnamed (default) value, which cannot be removed on every Windows
    /// version.</summary>
    private static void ClearValue(string keyPath, string? valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key is null) return;
        try
        {
            key.DeleteValue(valueName ?? string.Empty, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            AppStorage.Log($"NativeRedirect: clearing {keyPath}\\{valueName ?? "(default)"} failed: {ex.Message}");
        }
    }

    private static void TryDeleteEmptyKey(string keyPath)
    {
        _ = TryDeleteEmptyKeyCore(keyPath);
    }

    /// <summary>Returns true when the key is gone (deleted now or never
    /// existed), false when it still exists with content or could not be
    /// deleted.</summary>
    private static bool TryDeleteEmptyKeyCore(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key is null) return true;
            bool empty = key.SubKeyCount == 0 && key.ValueCount == 0;
            key.Close();
            if (empty)
            {
                Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppStorage.Log($"NativeRedirect: delete {keyPath} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Walks up from the patched key and removes the empty keys we created on
    /// the way (command, verb, shell, CLSID instance), so an uninstall leaves
    /// the class tree exactly as it was. Never removes the class root itself
    /// (Directory, Drive, Folder, CLSID).
    /// </summary>
    private static void TrimEmptyKeys(string keyPath)
    {
        const string root = @"Software\Classes\";
        if (!keyPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        string[] segments = keyPath[root.Length..].Split('\\');
        for (int count = segments.Length; count > 1; count--)
        {
            if (!TryDeleteEmptyKeyCore(root + string.Join('\\', segments, 0, count))) return;
        }
    }

    private static string SlotName(string keyPath, string? valueName)
        => keyPath.Replace('\\', '_') + (valueName is null ? "_default" : $"_{valueName}");
}
