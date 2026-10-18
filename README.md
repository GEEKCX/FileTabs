# FileTabs

Windows 多标签文件管理器——直接承载 **Windows 原生 Explorer 视图**，在它之上补齐标签、工作区、全盘搜索这些现代体验。文件列表、右键菜单、缩略图、预览面板全部来自系统本身，不是重新画的一套。

> A multi-tab wrapper around the real Windows Explorer shell view, with named workspaces and Everything-powered global search. Windows 10/11 x64.

## 下载

从 [Releases](https://github.com/GEEKCX/FileTabs/releases) 下载 `FileTabs-x.y.z-win-x64.exe`，单文件、免安装、自包含 .NET 运行时，下载后直接双击运行。

## 功能

- **多标签**：每个标签独立保存视图状态、滚动位置和历史；支持拖拽排序、复制标签、关闭其他 / 右侧标签。
- **会话恢复**：退出后所有标签原样还原，Ctrl+Shift+T 重开最近关闭的标签。
- **命名工作区**：把当前一组标签存成命名快照，之后一键切换。
- **Everything 深度整合**：Ctrl+F 聚焦搜索框，2 秒超时保护；结果可打开所在文件夹（自动复用已打开的同路径标签）、复制完整路径，或跳回 Everything 主程序。
- **书签栏**：把文件或文件夹拖进来即可，点击直达，支持在新标签打开 / 打开所在位置 / 重命名。
- **主题**：浅色 / 深色 / 跟随系统，窗口使用 Mica 背景。
- **单实例**：重复启动自动聚焦已开窗口。

## 快捷键

| 按键 | 功能 |
|---|---|
| `Ctrl+T` | 新标签 |
| `Ctrl+W` | 关闭当前标签 |
| `Ctrl+Shift+T` | 重开最近关闭的标签 |
| `Ctrl+F` | 聚焦 Everything 搜索 |
| `Ctrl+L` | 编辑地址栏 |
| `Ctrl+S` | 保存当前标签组为工作区 |
| `Ctrl+O` | 打开 / 切换工作区 |
| `F5` / `Ctrl+R` | 刷新 |

## 依赖

- **Windows 10 / 11 x64**
- **[Everything](https://www.voidtools.com/)**：可选，只影响全局搜索。没装或没开时，其他功能照常可用。

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
# Debug
dotnet build FileTabs.csproj

# Release（自包含单文件，输出到 bin\Release\net10.0-windows\win-x64\publish\）
.\publish.ps1
```

Everything 集成走官方 [Everything SDK](https://www.voidtools.com/forum/viewtopic.php?t=30541)（`Everything64.dll`，MIT 协议），Windows 上能正常调用 Everything 的 IPC。

## 致谢

- [Windows API Code Pack](https://github.com/aybe/Windows-API-Code-Pack-1.1) — ExplorerBrowser 封装
- [WPF-UI](https://wpfui.lepo.co/) — Fluent 设计系统控件
- [voidtools](https://www.voidtools.com/) — Everything 及其 SDK
