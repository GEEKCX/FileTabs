# Notes for future sessions

- `bin\` and `obj\` are build outputs and are listed in `.gitignore`; the project currently has no Git repository (checked 2026-10-18).
- `build_check\` is a one-off verification output created while the main `FileTabs.exe` was running and holding `bin\Debug\...\FileTabs.exe` locked. Safe to delete the whole folder.
- `dotnet` on `PATH` points to an SDK-less runtime (`C:\Program Files\dotnet`); the working SDK lives at `D:\Programs\dotnet\dotnet.exe`. Build with:
  `& "D:\Programs\dotnet\dotnet.exe" build FileTabs.csproj`
- Release build (self-contained single file, win-x64) is `.\publish.ps1`; output lands in
  `bin\Release\net10.0-windows\win-x64\publish\FileTabs.exe`. Test by launching the exe.
- UI uses WPF-UI (`ui:FluentWindow`, Mica backdrop); theme is driven by `ThemeManager.Apply`, keep `Fluent.*` brush keys in sync with `App.xaml`.
- Single instance is enforced by a `Local\FileTabs.SingleInstance` mutex in `App.xaml.cs`; a second launch activates the existing window via a named `EventWaitHandle`. When testing, launch the freshly built exe and make sure no old instance is left running before rebuilding (it locks the exe).
- User data lives under `%APPDATA%\FileTabs` (`session.json`, `bookmarks.json`, `workspaces.json`, `app.log`); corrupt JSON is archived as `.bad`.
