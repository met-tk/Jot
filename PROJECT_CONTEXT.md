# Jot (极简沉浸 WinUI 3 日记) - 项目上下文与跨会话交接

## 1. 核心定位与技术底线（<= 15 行）
- **项目定位**：Windows 11 本地优先极简沉浸日记，深度融合 Mica/Acrylic 质感、拟物音效与 Fluent 动效。
- **核心约束**：WinUI 3 (.NET 8, Windows App SDK), C# 12, MVVM, SQLite, Win2D。严禁未授权的外部云端依赖与强耦合业务逻辑。

## 2. 关键架构决策（ADR 索引，<= 15 行）
- **2026-10 / 架构解耦与服务治理**：拆解 `MainWindow`（抽取 `WindowPlacementManager`、`GlobalPaperAgingController`）、`EditorPage`（抽取 `PlatenHapticController`、`EditorUndoRedoManager`）、`AnalyticsViewModel`（抽取 `WordCloudAnalyzerService` 单篇增量缓存）；复用 `AuxiliaryCalendarStyler`、统一 `CjkTextHelper`；建立轻量级 `App.Services` 容器注册 `IDiaryStorageService` 与 `IDiaryIndexService`。
- **2026-10 / 性能与渲染升级**：`DiaryListPage` 升级为 `ItemsRepeater` UI 虚拟化与共享 `SharedCardContextFlyout`；`InkWeatheringEffect` 预渲染单周期表面 + `TransformMatrix` 滚动零重绘；`JotRichEditBox` 280ms 脉冲自休眠计时器；`EditorViewModel` 120ms 后台统计防抖。

## 3. 需求承接与待办清单（TODO，<= 25 行）
- [ ] [待办 1]：持续关注大型日记库下的内存表现与多窗口状态同步。
- [ ] [待办 2]：配合自动化验证套件（CV1-CV7）进一步收敛长文富文本交互细节。

