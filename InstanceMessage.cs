namespace FileTabs;

/// <summary>
/// Wire format for the single-instance pipe. The running instance has to know
/// whether a path came from its own Explorer view (the user is browsing: stay
/// in the current tab) or from another app (open a tab). The child process
/// detects that from its parent and tags the message.
/// </summary>
public static class InstanceMessage
{
    private const string NavigateInPlacePrefix = "nav|";

    public static string ForNewTab(string path) => path;

    public static string ForInPlaceNavigation(string path) => NavigateInPlacePrefix + path;

    public static bool TryParse(string? line, out string path, out bool navigateInPlace)
    {
        path = string.Empty;
        navigateInPlace = false;
        if (string.IsNullOrEmpty(line)) return false;

        navigateInPlace = line.StartsWith(NavigateInPlacePrefix, StringComparison.Ordinal);
        path = navigateInPlace ? line[NavigateInPlacePrefix.Length..] : line;
        return path.Length > 0;
    }
}
