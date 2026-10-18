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
