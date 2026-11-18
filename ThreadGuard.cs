using System.Diagnostics;
// WinForms (UseWindowsForms) also defines Application; the WPF one is meant.
using Application = System.Windows.Application;

namespace FileTabs;

/// <summary>
/// DEBUG-only assertions for code that is expected to leave the dispatcher.
/// Every method is [Conditional("DEBUG")], so Release builds pay nothing at
/// the call sites and the checks vanish from the compiled output.
/// </summary>
internal static class ThreadGuard
{
    [Conditional("DEBUG")]
    public static void AssertBackground(string? what = null)
    {
        if (Application.Current?.Dispatcher is { } d && d.CheckAccess())
            Debug.Fail($"阻塞操作在 UI 线程上执行：{what ?? "unknown"}");
    }
}
