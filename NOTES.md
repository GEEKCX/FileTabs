# Notes for future sessions

- `bin\` and `obj\` are build outputs and are listed in `.gitignore`; the project is a Git repository with remote `https://github.com/GEEKCX/FileTabs.git` (checked 2026-10-18).
- `build_check\` is a one-off verification output created while the main `FileTabs.exe` was running and holding `bin\Debug\...\FileTabs.exe` locked. Safe to delete the whole folder.
- `dotnet` on `PATH` points to an SDK-less runtime (`C:\Program Files\dotnet`); the working SDK lives at `D:\Programs\dotnet\dotnet.exe`. Build with:
  `& "D:\Programs\dotnet\dotnet.exe" build FileTabs.csproj`
- Release build (self-contained single file, win-x64) is `.\publish.ps1`; output lands in
  `bin\Release\net10.0-windows\win-x64\publish\FileTabs.exe`. Test by launching the exe.
- UI uses WPF-UI (`ui:FluentWindow`). `ThemeManager.Apply` forces `WindowBackdropType.None` and `WindowChrome.GlassFrameThickness = 0` on purpose: Mica/Acrylic extends the DWM glass frame over the hosted shell view, which then paints its labels white on the white list background. Theme brush keys come from two sources: project-specific ones declared in `App.xaml` (e.g. `Fluent.IconButton`) and WPF-UI's own keys inside `Wpf.Ui.dll` (e.g. `TextFillColorPrimaryBrush`, `ContextMenuBackground`); a missing `DynamicResource` key fails silently, so verify the key exists in the assembly before using a new one.
- Single instance is enforced by a `Local\FileTabs.SingleInstance` mutex in `App.xaml.cs`; a second launch activates the existing window via a named `EventWaitHandle`. When testing, launch the freshly built exe and make sure no old instance is left running before rebuilding (it locks the exe).
- User data lives under `%APPDATA%\FileTabs` (`session.json`, `bookmarks.json`, `workspaces.json`, `app.log`); corrupt JSON is archived as `.bad`.
- WPF `ContextMenu` can appear as a descendant of the main window in UIA even
  when there is no separate popup HWND. Enumerate the main window descendants
  and invoke `ShowDetailsPaneMenuItem` / `ShowPreviewPaneMenuItem` by
  `AutomationId`; screen-coordinate clicks are unreliable with DPI scaling.
- Verified the preview pane toggle both ways from `false`: settings persist to
  `ShowPreviewPane`, and the shell exposes `PreviewTitleAreaInner`,
  `PreviewThumbnailInner`, and `PreviewMetadataInner`.
- Tab strip uses Chrome-like tabs: the title bar is `SolidBackgroundFillColorBaseAltBrush`, inactive tabs `SolidBackgroundFillColorSecondaryBrush`, hover `SolidBackgroundFillColorBaseBrush`, the active tab `SolidBackgroundFillColorQuarternaryBrush` (white). Geometry is `Height=36`, `Margin="0,4,0,0"` (tabs touch, so only the rounded top corners meet), `MinWidth=200`, `MaxWidth=320`. The two 8x8 scoop paths hang *outside* the tab body (negative margin, `IsHitTestVisible="False"`) and are painted for every tab, so all tabs share the inverted bottom radius; the scoop of a neighbour that lands on the active tab is hidden underneath it because the active tab is drawn last (`Panel.ZIndex=10` in the item container style) with an opaque background. Verify pixel results with `bin\Release\capture-tabs.ps1 -TopHeight 96` (it sets per-monitor DPI awareness first; without that PrintWindow clips the capture).
- Window resize flicker (fixed 2026-10-23, `MainWindow.WndProcHook`): the hosted
  shell view sits in `WindowsFormsHost` (airspace), and every `WM_SIZE` of the
  size-move loop synchronously resized the `ExplorerBrowser` DirectUI view
  (~35-80 ms of UI-thread work per tick), so WPF could not repaint under the
  HwndHost and the hole showed the desktop wallpaper in ~15% of the captured
  frames. `WM_ENTERSIZEMOVE` now arms the loop, the first `WM_SIZE` with a
  changed client size collapses `ViewContainer` (HwndHost leaves layout, so no
  per-tick SetWindowPos reaches the shell), `WM_SIZE` outside the loop and a
  150 ms left-button watchdog both restore it. Move-only drags keep the body
  visible because the client size does not change. Measured after: 0 flicker
  frames in 3 repeated drags (was 15% of frames), shell restored and resized in
  every trial.
- Ctrl shortcuts were dead whenever the native shell view had focus
  (fixed 2026-10-23): the shell's DirectUI child HWND takes the keyboard, so
  `WM_KEYDOWN` never reached the main window and `OnPreviewKeyDown` never fired.
  `BrowserTab.ShellSubclassProc` now reports `WM_KEYDOWN` with Ctrl held through
  `ShellShortcutRequested`, and `MainWindow.HandleWindowShortcut` is the single
  implementation shared by the WPF key path, the window hook and the shell
  subclass. Only the chords the window owns (T/W/H/F/L/S/O/R/F5) are swallowed;
  Ctrl+A/C/V/X/Z and Ctrl+Shift+N still reach the shell, or file operations
  break. Verified with real key presses: Ctrl+T/W/F/L/S/O and Ctrl+Shift+T all
  work from `DirectUIHWND` focus, and Ctrl+A still selects files.
