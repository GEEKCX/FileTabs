# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The project is
[MIT licensed](./LICENSE).

## [1.3.1] - 2026-11-10

Everything 结果面板的类型/范围过滤，加上两个界面正确性修复。

### Added

- Everything 搜索结果支持按**文件类型**（文档 / 图片 / 视频 / 音频 / 压缩包 / 文件夹）和**范围**（此电脑、当前文件夹、盘符）过滤。

### Fixed

- 行复选框绘制不再触发动画裁剪，选中高亮在深色主题下保持文字可读。
- 目录清理页的陈旧统计与当前可选行保持同步，删除后自动重扫并更新计数。

## [1.4.0] - 2026-11-18

性能基建、注册表开关安全加固，以及工程自动化。借鉴 Arke 的性能实践，但保留原生 Explorer 视图路线。

### Added

- `--bench` 性能基准：quick/full 两档，报告 mean/p50/p95/min/max，setup（造数据）与 measure（实测）分列，支持 `--json` / `--out`。README 附实测表。
- `DirectoryListingCache`：地址栏目录枚举缓存（TTL 5s + 并发合并 + 2000 条上限），连续输入不再重复扫描。
- `ThreadGuard.AssertBackground`：`[Conditional("DEBUG")]` 的 UI 线程断言，Release 零开销，防止阻塞 IO 回到 UI 线程。
- GitHub Actions CI：push / PR 自动构建 + 跑全部测试（windows-latest，.NET 10）。
- LICENSE 改为 MIT；新增本 CHANGELOG。
- 注册表重定向开关支持 `--dry-run`：只读预览将写入的键，绝不修改注册表。

### Changed

- `FolderSizeCache` 增加 2000 条容量上限与过期清理（原来静态字典无上限，长跑会持续膨胀）。
- 版本号 1.3.1 → 1.4.0。

### Fixed

- 新建文件夹的重名探测原来在 UI 线程无上限循环，网络盘/离线 UNC 可能卡死；改为后台执行 + 1000 次上限 + 防重入。
- 控制面板页面（所有 CPL，含"电源选项"）不再被 Explorer 窗口捕获功能吞掉，路径解析后立即用 SW_SHOW 归还窗口。
- 缓存淘汰在并发 `Invalidate(null)` 时的竞态（可能抛 `Sequence contains no elements`），改为基于 `TryRemove` 的并发安全实现。

## [Unreleased]

<!-- 新改动追加到这里，按 Added / Changed / Fixed 分类。 -->