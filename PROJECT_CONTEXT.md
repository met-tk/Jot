# Jot (极简沉浸 WinUI 3 日记) - 项目上下文与最新进展

## 1. 架构与定位
- **定位**：Windows 11 本地优先极简沉浸日记，深度融合 Mica/Acrylic 质感、拟物音效与 Fluent 动效。
- **技术栈**：WinUI 3 (.NET 8, Windows App SDK 2.5.1, win-x64), C# 12, CommunityToolkit.Mvvm, SQLitePCLRaw, Win2D 1.3.2。
- **数据与索引**：标准 Markdown (`Documents/JournalData/Journals/{yyyy}/{MM}/{yyyy-MM-dd}.md`)；SQLite 增量索引；原子落盘配置 (`settings.json`)。

## 2. 核心模块清单与岁月沉淀架构 (ADR)
- `MainWindow.xaml(.cs)`：`GlobalPaperAgingHost` 覆盖全软件窗口底色层，通过 `CompositionBackdropBrush` 实施真·正片叠底 (Multiply) 与反向滤色 (Screen)，实时融入底色与 Mica。
- `Views/EditorPage.xaml(.cs)`：通过 `CompositionVisualSurface` 采样实际 `RichEditBox` 视口，并从其原生字形覆盖区域提取遮罩。灰度贴图转换为磨损 Alpha，合成时仅在磨损笔画处还原同一位置的干净纸面，形成实际墨迹脱落。亮暗模式分别过滤编辑器底色和蓝色选区；留白保留透明。纹理镜像重复，偏移随正文滚动。原生编辑器始终显示，聚焦与失焦不切换排版层。旧日记内容切换前预加载贴图，输入时仅更新原生控件与 GPU 合成，不重算整篇文字样式。
- `Services/AgingEngineService.cs`：一级时间衰减模型（当日及未来日记强度为零），纸纹灰度预处理与字迹灰度、透明磨损遮罩两级磁盘缓存。字迹遮罩 `wearmask-v4` 按贴图自身的对比度范围归一化；用户提供的低对比度贴图在 100% 强度下也能形成可见磨损。默认纹理在 `Assets/Textures`。
- `Views/SettingsPage.xaml` & `ViewModels/SettingsViewModel.cs`：提供独立双滑块（纸张底色强度 `AgingPaperIntensity` / 字迹风化强度 `AgingInkIntensity`），双贴图选择与缓存清理。
- `Models/AppSettings.cs`：支持独立纸墨强度配置与 300ms 磁盘防抖原子落盘。

## 3. 质量、验证与发布 (v1.3.0)
- **验证**：自包含 Release 发布构建通过；无警告、无错误。命令行验证 M1 10/10、M2 6/6、M3 8/8。M3 包含真实缓存像素检查、自定义贴图多种明暗与透明度检查、合成器效果图构建检查，以及亮暗模式下 Win2D 实际像素输出对字形、留白、编辑器底色和蓝色选区的检查。用户在旧预览版实际截图中确认字迹仅均匀褪色、纹理几乎不可见；原因是该自定义贴图的旧磨损缓存最大 Alpha 仅 54/255、平均约 11/255。新版按贴图分位数归一化后，预计最大 255/255、平均约 56/255。新版窗口效果仍须可视检查。
- **发布产物**：`Release\Publish\` 是此前版本，`Release\AgingPreview\` 是首个预览版；本次修正位于 `Release\AgingPreview-v2\`。
