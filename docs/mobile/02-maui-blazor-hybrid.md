# Jot Android 方案二：MAUI Blazor Hybrid 与 Web 编辑界面

文档版本：1.0｜编制日期：2026-10-16｜代码基线：`6d019b3`、Jot 1.2.1

本文为独立可实施方案；架构、接口和指标均为规划，不表示已经开发或真机验证。首发 Android，保留原 Windows 客户端；后续可以复用 Razor 组件扩展其他端，但浏览器版需要另做存储和身份设计。

## 1. 路线选择与产品定位

使用 MAUI 提供 Android 容器、文件访问、音频、生命周期和持久化；使用 BlazorWebView 承载随应用打包的 Razor、HTML、CSS 和编辑器资源。C# 业务在应用进程内执行，界面通过本地 WebView 展示。首版可以完全离线，不需要运行一个远程网站，也不是 Blazor Server 或 WebAssembly 客户端。[Blazor Hybrid](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/?view=aspnetcore-10.0)

本方案适合希望做统一排版、Markdown 预览、丰富搜索高亮，且团队具备 C# 与 Web 开发能力的情况。与 MAUI 原生方案相比，主要新增工作是 Web 编辑器的输入兼容、JS/.NET 通信、WebView 恢复和安全边界；收益是未来复用 Web UI 的能力。

BlazorWebView 和 HybridWebView 是不同组件。本方案选定 BlazorWebView，不同时引入 React/Vue 和另一套应用路由。只有单独替换编辑器等明确理由才引入局部 JavaScript 模块。[MAUI Hybrid 类型](https://learn.microsoft.com/en-us/dotnet/maui/hybrid-apps/)

若维护团队不愿持续维护 JavaScript、CSS 和 WebView 兼容矩阵，应选择 MAUI 原生方案。若关键中文输入在两周技术验证内仍不稳定，应停止把 Hybrid 作为主编辑路径。

## 2. 当前项目映射

| 当前文件与行为 | 迁移后的归属 | 必须补做 |
|---|---|---|
| `Journal.csproj`：.NET 8、WinUI、Windows RuntimeIdentifier | 原桌面工程保留，新建 MAUI 10 工程 | 新的 Android 打包和发布链 |
| `Services/DiaryStorageService.cs`：Markdown、快照、Trash | 原生 C# Repository | 可恢复提交、注入路径、草稿状态库 |
| `Services/DiaryIndexService.cs`：SQLite、UI 调度 | C# 索引与应用服务 | 去除 DispatcherQueue 类型依赖 |
| `ImportService.cs`、`ExportService.cs` | C# 用例 | 从路径入口改为 URI/流入口；备份保留原文 |
| `Views/EditorPage.xaml.cs`：RichEditBox 与 IME 处理 | JavaScript 编辑器适配器＋C# 会话 | 全部重新验证，不能复用 WinUI Document API |
| `ViewModels/AnalyticsViewModel.cs`：算法、画刷与字体混合 | 算法进入核心，外观进入 Razor/CSS | 移除显示对象、增量计算与取消 |
| `Models/AppSettings.cs`：窗口坐标、FontFamily 混合 | 通用偏好 DTO＋各端设置 | 不把 Windows 字体对象传给 WebView |
| `SoundEffectService.cs`：WinMM | Android 音效服务 | 不用 JS WebAudio 作为关键反馈路径 |

数据真源仍采用 `Journals/yyyy/MM/yyyy-MM-dd.md`。现有 SQLite 记录绝对路径，不能直接跨设备复制。当前全文搜索会读取正文，不能假设已经具备移动端全文索引；目前也不存在账户、支付或云同步模块。

## 3. 商用版本范围与交互设计

商用 1.0 包含今日/历史编辑、时间戳、撤销、草稿恢复、列表、全文搜索、月历、基础统计、导入导出、版本化备份、回收站、主题字号、保存提示音和支持入口。Markdown 预览可进入 1.0，但默认为纯文本编辑，预览失败不能阻止查看原文。

完整词云、复杂导入规则、丰富声效放在 1.1。自动同步、账户、购买权益、iOS 和浏览器发行是独立扩展。免费试用或付费下载可由发行方选择；本方案不预设订阅系统已经存在。

界面采用四入口“今日、日历、日记、更多”。编辑工具栏放时间戳、撤销和显式保存；原生状态条显示持久化确认和错误，不能仅由 JavaScript 自行显示“已保存”。导入用分步预览，恢复和删除有明确结果清单。所有页面覆盖初次使用、无数据、加载中、无结果、读写失败、权限失效、恢复中状态。

布局按可视区域随键盘变化更新，避免单纯使用固定 `100vh`；处理安全区、系统字体倍率和横竖屏。返回路径统一为键盘收起、子页面返回、应用退出，DOM 内路由与 MAUI 页面不能各自处理两次返回。TalkBack 必须能识别编辑区域、日期、按钮和保存状态；颜色、音效均不是唯一状态反馈。

## 4. 架构与项目组织

```text
Jot.Mobile.Hybrid
  MAUI 容器 / Android 平台服务 / BlazorWebView
       │                     │
       │               Jot.Ui.Razor
       │              页面 / CSS / JS 编辑器
       └──── Jot.Application ────┘
                  │
             Jot.Domain
                  │
           Jot.Infrastructure
      Markdown / SQLite / Backup / Draft
```

建议设置 `src/Jot.Mobile.Hybrid`、`src/Jot.Ui.Razor`、`src/Jot.Domain`、`src/Jot.Application`、`src/Jot.Infrastructure`。Razor 层只能调用按用例设计的应用服务，不直接获取任意路径、数据库连接、Keystore 或系统命令。

当前根目录 csproj 默认包含子目录源码。实施时先隔离新工程的编译和资源扫描范围，避免 Razor/移动端 C# 被现有 Windows 工程再次编译；这属于后续工程实施，不在本文档生成中执行。

MAUI 采用受支持的 .NET 10 / MAUI 10 组合；锁定 SDK、workload、NuGet、前端包和构建工具。共享核心与现有 Windows 工程的框架过渡需要单独验证：可以先提供无 UI 的 net8.0 兼容层，再在桌面运行时升级后统一目标，不能让手机框架强制决定桌面发布节奏。[MAUI 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/maui)

接口最少包含 `IDiaryRepository`、`IDraftStore`、`ISearchService`、`IImportService`、`IBackupService`、`IExternalDocuments`、`ISoundEffectService` 和 `IEditorSession`。用户界面只接收条目摘要、分页结果和错误码；正文仅在当前编辑/阅读用例中进入 WebView，避免把整个日记库复制到 JavaScript 内存。

Razor 渲染和编辑器 DOM 的所有权必须分离：编辑器根节点由专用 JS 模块维护，Blazor 不因计数器刷新而重建它。资源都随包发布，生产版不从 CDN 加载脚本、字体或编辑器插件。

## 5. 编辑器方案与通信协议

技术验证同时建立标准 textarea 基线和一个 CodeMirror 候选适配器。CodeMirror 是候选，不因桌面浏览器表现良好就承诺 Android IME 兼容；按当前官方维护入口和模块版本核验。官方旧 GitHub 仓库已指向新的维护站点，不能把仓库归档误判为产品停止维护。[项目官方入口](https://github.com/codemirror/dev)

最终只交付一个主编辑器，保留原生 MAUI Editor 作为恢复性简化编辑入口。必须通过中文拼音候选、手写/语音、Emoji、剪贴板、段落换行、选区替换和长文测试，才允许候选编辑器成为默认。Markdown 高亮可关闭，原始文本始终是持久化内容。

JS/.NET 通信定义版本化消息，而不是任意 JavaScript 方法调用集合：

| 字段/消息 | 约束 |
|---|---|
| `ProtocolVersion` | 与应用包匹配；不兼容时停止编辑并提供恢复入口 |
| `SessionId` | 每次打开日期生成新会话，迟到消息不得写入其他日记 |
| `EntryDate`、`BaseRevision` | 决定保存对象和冲突检查基线 |
| `Sequence` | 会话内单调递增；重复消息幂等，乱序消息拒绝或请求快照 |
| `EditBatch` | 内容变更、选区、组合输入状态；校验长度及偏移 |
| `Snapshot` | 完整内容及哈希，用于重建和序列断档修复 |
| `PersistAck` | C# 已将对应序号保存至草稿状态库 |
| `CommitAck` | 正文提交完成；附提交修订和序号 |
| `FlushRequest` | 切页/后台时请求尽力刷新，不能视为系统一定会给予时间 |

首版可以按小间隔发送完整快照以降低增量协议复杂度，但必须测量 100KB 和 1MB 文本开销；达到传输瓶颈后改用连续变更批次并定期校验快照，不能未经验证就引入复杂 CRDT。通信包有尺寸上限和超时，大消息分块时按整体哈希验证，失败重试不能形成重复插入。

编辑器可以在内存合并普通输入，不得在 composition 尚未完成时强行重置文本或插入时间戳。时间戳操作在确定选区后作为一个编辑事务执行。撤销由编辑器一侧拥有，C# 只记录持久化快照，避免双方同时维护互相冲突的撤销栈。

不得把每次输入全文同时写入 localStorage、IndexedDB 和 SQLite，造成三个不同真源。草稿以原生 C# 状态库为准，浏览器存储不承担正文可靠性保证。清理 WebView 缓存不应清理用户日记。

## 6. 保存状态机与 WebView 恢复

```text
编辑中 → 已交给原生层 → 草稿已保护 → 正文已保存
             │               │
           传输失败         提交失败
             └──── 可重试 / 原文导出 ────┘
```

“草稿已保护”必须收到 PersistAck，“已保存”必须收到 CommitAck。消息超时只表示状态未知，不能立即重复覆盖或清除脏标记。当前序号大于确认序号时，仍显示存在新修改。

原生保存过程固定日期、序号和基线修订；先事务写草稿与保存意图，再备份旧正文、同卷临时写与刷新、替换正文、更新索引、标记完成。文件与数据库之间通过恢复日志协调，不能声称一个数据库事务覆盖文件替换。正文已经写入而索引更新失败时，启动恢复补建索引，不回滚成旧正文。

建议草稿确认目标为停止输入后 P95≤500ms，正文保存约 1.2s 防抖。目标只涵盖应用已收到且成功落盘的内容，不包含输入法未提交文本，也不承诺硬件故障零丢失。生命周期回调只补充保护，不作为唯一保存点。[Android 进程生命周期](https://developer.android.com/guide/components/activities/process-lifecycle)

WebView 渲染进程异常需要与整个应用进程异常分别处理：宿主显示原生恢复页，销毁失效的 WebView 实例，新建实例并从最后确认草稿恢复；新实例创建新 SessionId，旧消息被忽略。连续失败两次则进入原生简化编辑器，允许继续写作和导出，保留经过脱敏的诊断信息。[Android WebView 管理](https://developer.android.com/develop/ui/views/layout/webapps/managing-webview)

默认不定时重建 WebView，不在页面间反复创建多实例；切换用户库、锁定或内存压力时按受控流程释放。页面隐藏不意味着 JS Timer 一定继续运行。

## 7. 数据格式、外部文件和备份

正文沿用 UTF-8 Markdown，逻辑日期不随系统时区变化而移动。索引保存相对路径；新增修订、父修订、内容哈希、UTC 修改时间和 schema 版本。索引可重建，草稿和恢复日志属于持久状态，不属于缓存。

修订元数据保存在持久状态或旁车文件，并进入备份 manifest，不能依赖可重建索引。哈希针对实际持久化字节，规范化换行或 BOM 必须有显式模式。保存、导入、恢复和未来同步共用按日期串行写入协调器，基线比较与提交在同一临界区。JS 传来的日期、修订和哈希只是输入，原生层必须验证，不直接信任。

外部文件通过 Android 系统选择器返回的 URI 读取流，后台解析，再把安全的结构化预览传给 Razor。不要向 JS 暴露原始本地路径或通用文件读取函数。[外部文档访问](https://developer.android.com/training/data-storage/shared/documents-files)

导入继承日期识别与跳过、覆盖、追加策略，但追加不是多端自动合并算法。导入原文保留到确认完成；记录每条操作状态，取消只停止未开始的条目，已完成条目可通过操作记录撤回。现有 20MB 单文件、100MB ZIP 上限作为初始值，增加实际解包字节、条目数、路径安全和超时限制。

备份 ZIP 包含 manifest、条目哈希、正文以及可选历史/草稿；备份从仓库读取，避免 JS 重新序列化正文。恢复先校验后预览，不直接覆盖整个库。已有导出会 Trim 首尾空白，商用备份需要新增保留模式，并用包含空行、BOM、不同换行和 Emoji 的数据夹具验证。

外部备份建立后记录成功时间和校验结果，不能把“打开分享面板”视为用户已存好备份。应用卸载会删除私有数据；用户文档位置的外部备份用于换机和卸载恢复。[应用私有文件](https://developer.android.com/training/data-storage/app-specific)

## 8. Web 安全与日记隐私

WebView 与原生 C# 之间存在信任边界，桥接消息必须校验；Web 技术并不自动获得原生文件安全性。[Blazor Hybrid 安全说明](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/security-considerations?view=aspnetcore-10.0)

实施要求如下：

- 页面加载只允许应用内资源；外部链接经用户操作交给系统浏览器，不在有桥接能力的页面中导航到任意网站。
- Markdown 预览默认禁用原始 HTML；若将来允许部分 HTML，采用允许列表清洗。脚本、事件属性、危险 URL 和远程追踪图片不得执行或自动加载。
- 使用适合实际 Blazor 资源的 CSP；禁止随意 eval，不为解决加载错误直接放开所有源。CSP 在 Release 包中验证。
- JS 接口只允许打开当前会话、编辑、请求预览等业务操作；不能提供任意文件路径、SQL、进程执行或反射入口。
- 令牌与密钥由原生层管理；后续登录走系统授权流程，不把长期凭据放入 JS 或浏览器存储。
- 生产版关闭 WebView 调试；日志不记录正文、标题、查询词、键盘事件或桥接消息正文。
- 对系统备份配置云端与设备迁移规则，排除未明确允许备份的日记、草稿及密钥；设置页面清楚解释范围。[Android 备份规则](https://developer.android.com/identity/data/autobackup)

日记在编辑时必然存在于 WebView 内存，不能宣称 JS 永远接触不到敏感内容。通过最少加载、禁止外部脚本和受控生命周期降低暴露。应用锁、沙盒、设备加密与端到端加密分别描述；若提供应用层加密，索引摘要、历史、草稿、导出和临时文件必须一并设计。

## 9. 搜索、统计、声音与后续同步

列表分页返回；搜索输入防抖并取消旧查询，结果按查询序号绑定。中文搜索先保持现有子串语义，若引入 FTS，需要验证分词和中文召回，不能仅替换 SQL 就宣称等价。统计在 C# 后台增量计算，Razor 只接收已聚合结果；词云限制词数并提供可访问的词频列表。

主题用少量 CSS 设计变量表达纸色、文字色和字号，系统默认字体优先。声音通过原生 SoundPool 服务，JS 仅发受限动作请求；重复消息不能重复播放保存音。保存提示音必须在保存成功后触发，后台或用户关闭时静音。手机音效不承诺捕获软键盘每个物理按键。[KeyEvent 限制](https://developer.android.com/reference/android/view/KeyEvent)

未来同步在原生应用层实现持久队列、幂等 ID、修订树/基线、删除墓碑和冲突副本。WebView 不持有常驻同步循环；不假设后台网页一直运行。同步内容是正文及元数据，SQLite 在各端独立维护。账户、服务端权限、传输加密、密钥恢复、备份保留和服务运维均是新增范围。

## 10. 实施里程碑与交付物

| 阶段 | 实施内容 | 交付/退出标准 | 历时 |
|---|---|---|---|
| G0 编辑器验证 | textarea/候选编辑器、中文 IME、100KB 长文、Release WebView | 两家 OEM 上输入可靠，恢复原生简化入口可用 | 1～2 周 |
| G1 核心与协议 | Repository、草稿日志、消息版本、ACK、重放测试 | 保存中断和乱序消息不造成覆盖 | 2～3 周 |
| G2 完整产品 | Razor 页面、导航、搜索、月历、导入、备份、回收站 | P0 场景闭环，离线可用，无 CDN 依赖 | 4～5 周 |
| G3 加固 | Web 安全、WebView 崩溃恢复、性能、无障碍、升级 | 商用验收通过，安全测试没有越界桥接 | 3～4 周 |
| G4 发行 | 封闭测试、安装升级、材料、灰度观察 | 候选包可追溯，无数据完整性阻断项 | 2～4 周 |

估算主开发约 60～90 人日，专项测试 20～30 人日，设计/产品 8～12 人日，发行支持 5～8 人日，总计约 93～140 人日；部分工作并行时商用版约 12～18 周。前提是主开发熟悉 C#、Blazor 与前端编辑器；学习成本未包含。预算按角色单价计算，另留约 25% 兼容性储备，外部服务和商店费用另列。

G0 不通过时优先回到 MAUI 原生主编辑路线，保留可复用的 C# 核心；不要为了维持路线而永久保留两套完整界面。若只是 Markdown 预览失败，可以降级为纯文本；若出现已确认草稿恢复失败，必须阻止发布。

## 11. 商用测试与可量化验收

产品最低 Android 10 为规划建议，与框架最低版本区别开。设备覆盖 4GB 中低端 ARM64 真机、两个以上 OEM、当前目标系统、历史支持系统；WebView 至少覆盖当前稳定版本和仍被支持的较旧版本。国内无 Google 服务设备按实际 WebView 提供者实测，不能简单要求所有用户安装同一种浏览器。

| 验证领域 | 必须验证的条件 |
|---|---|
| 编辑与组合输入 | 拼音候选不被刷新打断，语音与长按粘贴不丢字，撤销边界正确 |
| 消息可靠性 | 重复、乱序、丢 ACK、超时、旧 SessionId、非法选区、超限负载 |
| 双进程恢复 | 单独终止 WebView 渲染器、终止应用、后台回收、重启设备 |
| 持久化 | 磁盘满、临时文件残留、索引损坏、快照失败、快速换日 |
| 内容安全 | 含 HTML/脚本/危险链接的 Markdown，桥接越权，路径穿越 ZIP |
| 数据交换 | 桌面往返、重复恢复、格式升级、损坏备份、首尾空白保留 |
| 无障碍 | TalkBack 导航编辑、200% 字号、系统减少动画、键盘遮挡 |
| 性能目标 | 冷启动可编辑 P95≤4s；100KB 文本输入反馈 P95≤50ms；草稿 ACK P95≤500ms；10000 篇热索引搜索 P95≤500ms |

以上性能基准使用同一台明确型号的参考设备、Release 包、相同数据集重复测量并保存分布，不能把桌面浏览器结果替代真机。1MB 文本列为压力用例，超过主编辑能力时必须提供完整保留和简化编辑路径，不静默截断。

持续集成除 .NET 测试外，增加消息协议测试、JS 编辑事务测试、静态资源构建、依赖许可/漏洞检查和 CSP 验证。浏览器自动化覆盖 DOM 逻辑，Android 真机测试覆盖 IME 和生命周期；二者不能相互替代。

## 12. 发布、运维与最终交接

固定发行方认可的应用 ID、签名身份、versionCode 递增规则；凭据不进入代码库。打包 AAB/APK 后检查 ARM64 与实际 .so 的 16KB 对齐，包括 .NET 运行时、SQLite 和其他本地组件；核对提交当日 target API 要求。[Android 页大小](https://developer.android.com/guide/practices/page-sizes)、[Play target API](https://developer.android.com/google/play/requirements/target-sdk)

Web 静态资源版本与应用包版本一起发布，不通过远程脚本热替换业务逻辑。灰度观察崩溃、WebView 重建次数、保存失败和恢复失败；默认不收集用户正文。任何确认的数据损坏触发停止扩大灰度，必要时发布更高 versionCode 修复包，不能假设可以降级恢复旧安装包。

若用户环境中的 WebView 不兼容，展示具体修复建议，同时允许原生简化编辑和备份。不能以升级 WebView 为由阻止用户取回已有日记。客服处理顺序是保护草稿、导出、诊断、修复，不能首先要求卸载。

交付物包括安装包及哈希、源码提交、SDK/NuGet/前端依赖锁、协议说明、内容安全测试、真机报告、数据格式与迁移说明、备份恢复用户手册、第三方授权清单、故障码和支持流程。产品负责人确认功能与收费边界，技术负责人确认依赖与恢复机制，测试负责人签署验收，发行负责人完成渠道材料和灰度决策。

## 13. 可直接拆分的首批实施任务

| 编号 | 任务与责任角色 | 依赖 | 完成证据 |
|---|---|---|---|
| B01 | 主开发：独立 Hybrid 工程及本地资源 Release 打包 | 无 | 断网启动与 Windows 原工程回归 |
| B02 | 前端/主开发：textarea 与候选编辑器输入验证 | B01 | 两家 OEM 的 IME 和长文对照 |
| B03 | 主开发：编辑会话与消息 schema、序号/ACK 实现 | B01 | 乱序、重复、超时和越界输入测试 |
| B04 | 主开发：纯 C# Repository 与持久草稿、恢复日志 | 无 | 故障注入与日期串行写入证明 |
| B05 | 主开发：WebView 渲染器恢复和原生简化编辑入口 | B03、B04 | 单独终止渲染器后的恢复录像 |
| B06 | 主开发＋测试：安全预览、链接限制、CSP 与桥接校验 | B02、B03 | 恶意内容夹具不能越界访问 |
| B07 | 主开发＋测试：SAF 导入、版本化备份、桌面往返 | B04 | 备份校验与恢复结果清单 |
| B08 | 产品＋设计：Razor 页面和导航/键盘规范 | B02 | P0 状态稿与可访问性检查 |

前端角色可由同一名主开发兼任，但相关人日不能因此从预算中消失。选定主编辑器后锁定其版本和适配契约，不在页面开发中频繁更换引擎。
