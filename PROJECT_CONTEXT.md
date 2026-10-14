# Journal (极简沉浸 WinUI 3 日记) - 项目上下文

## 1. 架构与定位
- **定位**：Windows 11 本地优先极简沉浸日记软件，深度融合 Mica/Acrylic 质感、打字机拟物音效与 Fluent 动效。
- **技术栈**：WinUI 3 (.NET 8/9, Windows App SDK 2.5, win-x64), C# 12, CommunityToolkit.Mvvm, SQLitePCLRaw。
- **数据真理源**：标准 Markdown (`Documents/JournalData/Journals/{yyyy}/{MM}/{yyyy-MM-dd}.md`)。
- **索引与配置**：本地 SQLite (`journal_index.db`) 驱动日历与统计；原子配置 (`Documents/JournalData/settings.json`)。

## 2. 核心模块清单
- `MainWindow.xaml(.cs)`：融入式标题栏 (`ExtendsContentIntoTitleBar`)、Mica/Mica Alt/Acrylic 动态材质与背景通透度热切。
- `Views/ShellPage.xaml(.cs)`：64px 悬浮微透胶囊导航岛，支持快捷键 (Ctrl+1~5) 路由至编辑、日历、列表、统计、设置。
- `Views/EditorPage.xaml(.cs)`：纯文本 RichEditBox 黄金排版，Composition 0-GC 击键微震 (1.1px/2.2px)，正弦呼吸脉冲光标，IME 拼音捕获。
- `Views/DiaryListPage.xaml`：自适应卡片时间线，首句诗意提炼（Headline）去冗，支持关键词实时检索。
- `Views/CalendarPage.xaml`：数学无重叠日历面板 (`CalendarGridPanel.cs`)，年热力色阶与月视图特写。
- `Views/AnalyticsPage.xaml`：连击打卡统计、字数看板与多维批量导出（日期范围/按月导出为 Markdown+TOC / ZIP / PDF）。
- `Views/SettingsPage.xaml` & `ViewModels/SettingsViewModel.cs`：Fluent 卡片设计，打字音效切换、手动保存音效预设、音量、字纸震动、字体与材质定制。
- `Services/SoundEffectService.cs` & `PcmAudioSynthesizer.cs`：16-bit 44.1kHz PCM 声学建模与实录采样（12 款主题 × 14 变体防重复洗牌池 + 手动保存铜铃预设），WinMM WaveOut 多通道混音。
- `Services/AppSettingsService.cs`：`_timerLock` + 300ms 磁盘防抖 + `.tmp` 原子落盘与内存即时分发。
- `Services/ExportService.cs` & `DiaryStorageService.cs`：多维导出引擎与原子读写保障。

## 3. 质量与数据安全保障（消费级水准）
- **构建状态**：`dotnet build` (Debug & Release) 双模式 0 警告 0 错误。
- **自动化测试**：`Jot.exe --verify-m4` 26/26 项全绿通过 [PASS]（包含消费级快照容灾、软删除回收站、词云精确词频与向心排布、12 款多变体洗牌与手动保存音效测试）。
- **安全审计**：`code-auditor` 终审评定 **[PASS]**，零内存泄漏、零隐式误删、无锁争用。
- **词云与意象图谱**：全量日记单次真实计次（消除标题/摘要/正文重复叠加与采样断层）；大词居中、小词环绕的向心交织排布；幂函数字号阶梯（12.5px ~ 32.5px）与动态字重。
- **消费级容灾与零丢失保障**：
  - **自动安全快照**：覆盖写入与自动保存前，自动在 `Documents/JournalData/Backups/` 生成带时间戳备份，滚动安全修剪。
  - **物理软删除回收站**：彻底根除裸 `File.Delete` 抹杀，单篇与批量删除统一移交 `Documents/JournalData/Trash/` 回收站。
  - **孤儿条目与空白日记防御**：物理文件存在即绝不删除数据库索引；编辑器空白草稿安全保留原内容；彻底消除 `2026-04-01` 因测试删除与字数误杀导致的蒸发元凶。
  - **Windows 并发文件锁防护**：日记只读流升级为 `FileShare.ReadWrite | FileShare.Delete`，杜绝后台保存原子替换时因瞬时读取触发 Sharing Violation。
- **体验与视觉质感**：系统材质（Solid / Mica Alt / Mica / Acrylic）与 6 大柔和沉浸色板正交隔离；左侧菜单高亮独立画刷直通，杜绝预设间颜色入侵与错乱；卡片自适应微透联动。
- **发布契约**：大功能更新同步自增版本号；当前最新发布版本为 **v1.2.0**（安装包产物位于 `Release\Jot_Setup_v1.2.0.exe`）。
