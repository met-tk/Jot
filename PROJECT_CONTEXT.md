# Jot (极简沉浸 WinUI 3 日记) - 项目上下文

## 1. 架构与定位
- **定位**：Windows 11 本地优先极简沉浸日记软件，深度融合 Mica/Acrylic 质感、打字机拟物音效与 Fluent 动效。
- **技术栈**：WinUI 3 (.NET 8, Windows App SDK 1.5, win-x64), C# 12, CommunityToolkit.Mvvm, SQLitePCLRaw。
- **数据真理源**：标准 Markdown (`Documents/JournalData/Journals/{yyyy}/{MM}/{yyyy-MM-dd}.md`)。
- **索引与配置**：本地 SQLite (`journal_index.db`) 驱动日历与统计；原子配置 (`Documents/JournalData/settings.json`)。

## 2. 核心模块清单
- `MainWindow.xaml(.cs)`：融入式标题栏 (`ExtendsContentIntoTitleBar`)、Mica/Mica Alt/Acrylic 动态材质与背景通透度热切，支持退出前保存/放弃草稿确认与多屏坐标自适应记忆。
- `Views/ShellPage.xaml(.cs)`：64px 悬浮微透胶囊导航岛，支持全局快捷键 (`Ctrl+1~6` 及小键盘 `Ctrl+NumPad1~6`) 与鼠标侧键路由至编辑、日历、目录、统计、导入、设置。
- `Views/EditorPage.xaml(.cs)`：纯文本 RichEditBox 黄金排版，Composition 0-GC 击键微震 (1.1px/2.2px)，IME 拼音捕获与受控多步 Undo/Redo。
- `Views/DiaryListPage.xaml(.cs)`：自适应卡片时间线，首句诗意提炼（Headline）去冗，支持全文与日期关键词实时检索高亮及多选批量导出/删除。
- `Views/CalendarPage.xaml(.cs)`：数学无重叠日历面板 (`CalendarGridPanel.cs`)，支持年/月/日三级钻取、热力色阶与 `Ctrl+滚轮` 缩放。
- `Views/AnalyticsPage.xaml(.cs)`：连击打卡统计、字数梯队、24 小时创作生物钟、近 12 个月自适应趋势看板，以及后台异步 $O(N \cdot L)$ 词云图谱与穿透检索。
- `Views/ImportPage.xaml(.cs)` & `Services/ImportService.cs`：支持拖拽/选择多源文本、Markdown、JSON、ZIP 归档智能日期切分、实时并排/合并预览与冲突策略。
- `Views/SettingsPage.xaml(.cs)` & `ViewModels/SettingsViewModel.cs`：Fluent 卡片设计，打字音效切换、手动保存音效预设、音量、字纸震动、字体与材质定制。
- `Services/SoundEffectService.cs` & `PcmAudioSynthesizer.cs`：16-bit 44.1kHz PCM 声学建模与实录采样（12 款主题 × 14 变体防重复洗牌池 + 手动保存音效预设），POH 零分配就地音量缩放与 WinMM WaveOut 多通道混音。
- `Services/AppSettingsService.cs`：`_timerLock` + `_stateLock` 线程安全克隆 + 300ms 磁盘防抖 + `.tmp` 原子落盘与内存即时分发。
- `Services/ExportService.cs` & `DiaryStorageService.cs`：多维导出引擎与原子读写、增量索引同步及自动备份保障。

## 3. 质量与数据安全保障
- **增量索引与单次查询复用**：`DiaryIndexService.FullScanAndSyncAsync` 基于文件路径与 `LastWriteTime` 执行增量同步，仅解析新增或变更的日记文件；日历与统计面板复用同一批内存索引，避免重复全表扫描。
- **消费级容灾与零丢失保障**：
  - **自动安全快照**：覆盖写入与自动保存前，自动在 `Documents/JournalData/Backups/` 生成带时间戳备份，后台异步滚动修剪。
  - **物理软删除回收站**：彻底根除裸 `File.Delete` 抹杀，单篇与批量删除统一移交 `Documents/JournalData/Trash/` 回收站。
  - **孤儿条目与空白日记防御**：物理文件存在即绝不删除数据库索引；退出时若选择“不保存退出”立即清除未保存标志，防止页面卸载回调误覆盖磁盘原文件。
  - **Windows 并发文件锁防护**：日记只读流统一使用 `FileShare.ReadWrite | FileShare.Delete`，杜绝后台保存原子替换时因瞬时读取触发 Sharing Violation。
- **发布契约**：大功能与性能优化同步自增版本号；当前最新发布版本为 **v1.2.1**（安装包产物位于 `Release\Jot_Setup_v1.2.1.exe`）。
