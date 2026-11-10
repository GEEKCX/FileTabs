# FileTabs

Windows 多标签文件管理器——直接承载 **Windows 原生 Explorer 视图**，在它之上补齐标签、工作区、全盘搜索这些现代体验。文件列表、右键菜单、缩略图、预览面板全部来自系统本身，不是重新画的一套。

> A multi-tab wrapper around the real Windows Explorer shell view, with named workspaces and Everything-powered global search. Windows 10/11 x64.

## 下载

从 [Releases](https://github.com/GEEKCX/FileTabs/releases) 下载两种发布之一：

- **安装版（推荐）**：`FileTabs-x.y.z-setup.exe`，Inno Setup per-user 安装到 `%LOCALAPPDATA%\Programs\FileTabs`，无 UAC 弹窗，包含开始菜单和桌面快捷方式，卸载时自动恢复原生 Explorer 的文件夹打开方式。
- **便携版**：`FileTabs-x.y.z-win-x64.exe`，单文件、免安装、自包含 .NET 运行时，下载后直接双击运行。

## 功能

- **多标签**：每个标签独立保存视图状态、滚动位置和历史；支持拖拽排序、复制标签、关闭其他 / 右侧标签。
- **会话恢复**：退出后所有标签原样还原，Ctrl+Shift+T 重开最近关闭的标签。
- **命名工作区**：把当前一组标签存成命名快照，之后一键切换。
- **Everything 深度整合**：Ctrl+F 聚焦搜索框，2 秒超时保护；结果可打开所在文件夹（自动复用已打开的同路径标签）、复制完整路径，或跳回 Everything 主程序。
- **书签栏**：把文件或文件夹拖进来即可，点击直达，支持在新标签打开 / 打开所在位置 / 重命名。
- **扩展中心**：内置可扩展的功能聚合页，窗口打开时会把当前目录和选中项交给功能页面。
  - **目录模板**：把重复的目录结构存成模板，支持 `{名称}`、`{序号}`、`{日期}`、`{月份}` 变量；输入多行（或逗号分隔）可批量生成多棵目录树，实时预览、已存在目录自动跳过。
  - **批量重命名**：按模板给选中项改名，支持 `{原名}`、`{原文件名}`、`{扩展名}`、`{序号}`、`{日期}`；执行前逐项预览并检测重名、非法字符和已存在目标，改名走系统文件操作，成功后可在页面内撤销上一次。
  - **目录清单**：把当前目录（可选递归、可选隐藏项）导出为 CSV、Markdown 或 JSON；扫描在后台进行，可取消。
  - **目录清理**：一次扫描列出空文件夹、最大的 N 个文件、超过指定天数未修改的文件；删除统一进回收站，删除后自动重扫。
- **拖放中转（DropPoint）**：Shift+CapsLock 在鼠标所在屏幕唤起临时托盘点，拖入文件后从托盘点拖出到任意目标（微信、浏览器上传等），跟随深浅色主题。
- **主题**：浅色 / 深色 / 跟随系统，窗口使用 Mica 背景。
- **单实例**：重复启动自动聚焦已开窗口；常驻托盘便于找回主窗口与退出。

## 数据与恢复

数据默认保存在 `%APPDATA%\FileTabs`，可以在 **设置 → 数据与恢复 → 打开数据目录** 直接打开。主要文件包括 `session.json`（标签会话）、`bookmarks.json`、`workspaces.json`、`folder-templates.json`、`rename-history.json`、`settings.json` 和 `history.json`。

每次保存会把上一版保留为同名 `.bak`（例如 `session.json.bak`）。每个文件只保留一版；下一次保存会覆盖旧的 `.bak`。

- **自动恢复**：启动或读取时如果主文件缺失、损坏，会自动读取 `.bak`，把损坏的主文件另存为 `.bad`，再用备份恢复主文件。使用备份会记录到 `app.log`。
- **手动恢复**：打开 **设置 → 数据与恢复 → 恢复最近备份**，确认后 FileTabs 会自动重启并加载恢复的数据。也可以关掉 FileTabs 后，在数据目录里把 `<文件名>.bak` 复制成 `<文件名>`（例如把 `bookmarks.json.bak` 复制为 `bookmarks.json`）。
- **注意**：“恢复最近备份”会用 `.bak` 覆盖当前文件，当前内容不会另存；确认前先打开数据目录备份需要的文件。
- **崩溃记录**：UI 线程未处理异常会先同步保存当前会话，再提示“重启并恢复 / 打开数据目录 / 退出”；非 UI 线程异常写入 `crash.log`。`app.log` 和 `crash.log` 各保留一份上一版，超过 1 MB 时轮转为 `.1`。

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
| `Shift+CapsLock` | 唤起拖放中转托盘点（可在设置关闭） |
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

# 测试（本机未配置 NuGet 源时，先加 --source https://api.nuget.org/v3/index.json 还原）
dotnet test tests/FileTabs.Tests/FileTabs.Tests.csproj

# Inno Setup 安装包（输出到 installer\FileTabs-x.y.z-setup.exe）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer.iss
```

Everything 集成走官方 [Everything SDK](https://www.voidtools.com/forum/viewtopic.php?t=30541)（`Everything64.dll`，MIT 协议），Windows 上能正常调用 Everything 的 IPC。

## 致谢

- [Windows API Code Pack](https://github.com/aybe/Windows-API-Code-Pack-1.1) — ExplorerBrowser 封装
- [WPF-UI](https://wpfui.lepo.co/) — Fluent 设计系统控件
- [voidtools](https://www.voidtools.com/) — Everything 及其 SDK
