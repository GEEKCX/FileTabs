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
- Tab strip uses Chrome-like tabs: the title bar is `FileTabs.ChromeFillBrush`, inactive tabs `FileTabs.TabInactiveBrush` (same color as the title bar, so only the active tab's shape reads), hover `FileTabs.ChromeHoverBrush`, the active tab `FileTabs.TabActiveBrush`. Geometry is `Height=30`, `Margin="-8,6,-8,0"` and `Width=TabWidth` (= body width + 8+8), with the tab body inset by `Margin="8,0"`; `UpdateTabWidth` therefore computes the *body* width and adds `2 * TabScoopWidth` for the item. The two 8x8 scoop paths are painted for every tab so all tabs share the inverted bottom radius; the scoop of a neighbour that lands on the active tab is hidden underneath it because the active tab is drawn last (`Panel.ZIndex=10` in the item container style) with an opaque background.
  The scoops must stay **inside** the item's own bounds (the item is 8px wider than the body on each side and the neighbouring items overlap by 8px). Placing them with a negative margin instead makes WPF give the item's `Grid` a layout clip equal to its own arranged size (`LayoutInformation.GetLayoutClip`), and the overflow is then never painted — that is why the inverted corners were invisible in 1.1.6 (fixed 2026-10-25). The scoop markup itself has not changed since the first commit, so what broke them was the tab sizing rework in 1.1.0/1.1.1 (explicit `Width=TabWidth` plus `MinWidth`/`MaxWidth` on the tab item). A sibling `Border Background="Transparent" Margin="8,0"` carries the hit area so the 8px overlaps do not steal the neighbour's hover and clicks.
  Verify pixel results with `bin\Release\capture-tabs.ps1 -TopHeight 70` (it sets per-monitor DPI awareness first; without that PrintWindow clips the capture) plus `bin\Release\probe.ps1`/`zoom.ps1` for the pixel rows and magnified crops. The window is 1550.4 DIP at 125% scaling, so 1 DIP = 1.25 px in the capture and the tab strip starts at x≈60 px.
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
- Selection & status bar perf (fixed 2026-10-31): `UpdateStatusBar` only shows the custom bar for an all-folder selection, yet the old loop read `System.Size` via COM for every non-folder item — Ctrl+A over thousands of files paid thousands of UI-thread COM reads just to hide the bar. The loop now breaks on the first non-folder item (mixed selection is O(1) shell reads). `BrowserTab.SelectionChanged` also fires in storms while rubber-band dragging (dozens–hundreds per drag); like ItemsChanged it now coalesces through a `_selectionChangedPending` dirty flag dispatched at Background priority, so `UpdateStatusBar`/`SyncViewMenuChecks`/`UpdateRenameButton` run at most once per UI cycle.
- DropPoint P0 acceptance (verified 2026-10-31): 12 PRD criteria checked; 9 pass by code inspection, #3 (drag into WeChat/browser) still needs a human drag test. Real bug found & fixed: pads snapshot theme brushes at construction, so an open pad kept the old palette after a theme switch (PRD #8). `ThemeManager` now raises `ThemeApplied` after `Apply`, and `DropPointWindow.RefreshThemeBrushes` re-reads root/card/primary/secondary/badge/icon/close brushes (guard `_isMoving` skips repaint mid-drag; subscription is removed in `Closed`). PRD #7 wording updated instead of code: since 1.1.6 the tray is persistent for window recovery/exit, so it no longer hides with the DropPoint toggle. PRD #6 actually passes: the main window has no cancel-on-close and ShutdownMode is the default OnLastWindowClose, so closing the window exits the process — hot-key dies with it.
- DropPoint regression script (A2, 2026-10-31): `scripts\droppoint-regression.ps1` drives a live instance — closes stale pads via WM_CLOSE, sends Shift+CapsLock x4 through `keybd_event` (ModShift|ModNoRepeat: shift down, caps tap, shift up; CapsLock state restored), then asserts the app.log delta: one `pad shown ... slot=0` creation + >=3 `reused existing pad` lines (PRD #10: empty pads are reused, so slots 1/2 and the limit only trigger with occupied pads — those need a human drag). Ends with a PrintWindow capture of the pad into `verify-droppoint\`. Gotchas baked in: the .ps1 MUST be saved as UTF-8 with BOM or PowerShell 5.1 parses the Chinese window-title match as ANSI and never finds pads; FindWindowW only returns one match, so pads are enumerated via EnumWindows filtered by PID + title. Exit 0 = pass. Human-only remainder: #2/#3/#5 OLE drag in/out, #7 toggle, #8 theme, #11 stair with 2-3 occupied pads, #12 remembered position.
- Extensions center framework (Sprint 1, 2026-10-31): `ExtensionsWindow` is now a generic shell — left nav ListBox (icon+title+description from `ExtensionFeature` record), right `ContentControl` hosting lazily-created cached pages. Features register in `ExtensionsRegistry`'s static constructor (`Register(Id, Title, Description, Icon, owner => new Page(owner))`; duplicate Id throws). Folder templates moved wholesale into `FolderTemplatePage` — zero behavior change, `MainWindow.xaml.cs` call site untouched (`initialFolder` in, `CreatedFolderPath` out, both preserved). Next feature (batch rename etc.) = one `Register` call + one UserControl. No plugin loading by design.
