<div align="center">
  <img src="https://raw.githubusercontent.com/met-tk/Jot/main/Assets/jot.png" width="110" height="110" alt="Jot Logo" />
  <h1>Jot</h1>
  <p><strong>A simple and elegant journaling app · 纯粹、优雅的桌面沉浸式日记应用</strong></p>
  <p>
    <a href="https://github.com/met-tk/Jot/releases/latest">
      <img src="https://img.shields.io/badge/Release-v1.4.0-107C41?style=flat-square" alt="Release v1.4.0" />
    </a>
    <img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-0078D4?style=flat-square" alt="Platform" />
    <img src="https://img.shields.io/badge/Framework-.NET%208%20%2B%20WinUI%203-512BD4?style=flat-square" alt="Framework" />
    <a href="https://space.bilibili.com/24340298">
      <img src="https://img.shields.io/badge/AI--Driven%20by-Taketo-FB7299?style=flat-square" alt="Author" />
    </a>
  </p>
</div>

---

## ✨ 简介 (Overview)

**Jot** 是一款基于 **WinUI 3 (.NET 8)** 打造的现代化 Windows 桌面日记软件。它专注于纯粹无干扰的沉浸式书写体验，将**拟真物理击键声学反馈**、**灵活的云母/亚克力材质与氛围色调**、**多维日历与词云数据洞察**以及**100% 本地优先的纯文本（Markdown）存储**融为一体。

---

## 🖼️ 界面预览 (Screenshots)

### 1. 今日随记 · 沉浸式书写空间
简洁专注的纸面编辑体验，支持一键插入 `[HH:mm:ss]` 时间戳（`Ctrl+T`）、自动保存与草稿保持、纸面微震动回弹，以及 12 种多变体物理击键音效与手动保存音效。

![今日随记](https://raw.githubusercontent.com/met-tk/Jot/main/%E7%A4%BA%E4%BE%8B/19cb3e18bdf75c6e12bc6c0d9c63e7df.png)

---

### 2. 日历透视 · 全年热力总览与单日阅读
42 格紧凑数学排布的 12 个月全年视图，根据每日字数自动映射热力深浅；点击任意日期即可进入沉浸式阅读视图，并与右侧当月小日历实时联动。

![日历全年视图](https://raw.githubusercontent.com/met-tk/Jot/main/%E7%A4%BA%E4%BE%8B/823ba1d503e8eb57744e4f62182bcb2a.png)

![日历单日阅读](https://raw.githubusercontent.com/met-tk/Jot/main/%E7%A4%BA%E4%BE%8B/cf1adbfa2953246a5ef9eef06b55437e.png)

---

### 3. 目录时间线 · 检索与批量归档
按年月折叠/展开的时间线卡片流，支持全文与日期即时搜索、右键单篇一键复制内容，以及多选批量导出为独立 `.md`、`.txt` 或 `.json` 归档。

![目录时间线](https://raw.githubusercontent.com/met-tk/Jot/main/%E7%A4%BA%E4%BE%8B/c18b8c9f3415757adb703ee354eeb700.png)

---

### 4. 数据与统计 · 黄金螺旋词云与书写节律
内置 4 大核心看板指标、黄金分割角螺旋排布的高频词云（支持点击词汇穿透筛选对应日记并高亮关键词）、5 级篇幅分布、24 小时创作时段分布与近 12 个月书写趋势。

![数据与统计看板](https://raw.githubusercontent.com/met-tk/Jot/main/%E7%A4%BA%E4%BE%8B/070e25acee6c9bb5a24309a2b10259c5.png)

---

### 5. 日记导入工作台 · 智能切分与双模预演
支持批量导入 `.md`、`.txt`、`.json`、`.zip` 文件，内置智能日期锚点探测与自定义正则切片，提供**左右对照**与**合并预览**双视图及三种冲突解决策略（智能追加合并 / 覆盖更新 / 跳过）。

![日记导入工作台](https://raw.githubusercontent.com/met-tk/Jot/main/%E7%A4%BA%E4%BE%8B/1887330de4ff5d2c12493d3d68ca30a0.png)

---

## 🚀 核心特性 (Key Features)

- **🎹 12 款多变体沉浸式声学反馈 + 手动保存音效预设**：内置复古打字机（含回车黄铜铃声）、Cherry 青轴/茶轴/红轴、客制化 HiFi 麻将音、空山听雨（青石水滴）、怀旧薄膜键盘、静修木鱼（檀木禅敲），以及古董重锤打字机、经典机械打字机、iPhone 触屏按键、矩阵数据流 4 款实录采样主题；每款主题配备 10 组普通击键与 3 组大键微差变体及防重复洗牌引擎，并支持手动保存（`Ctrl+S` / 保存按钮）独立音效预设（如打字机清脆铜铃）、中文输入法整句上屏音效与独立音量调节。
- **🎨 4 种窗口材质 × 6 套氛围色调**：
  - **系统材质**：`Mica Alt`（深层云母）、`Mica`（原生云母）、`Acrylic`（亚克力毛玻璃）、`Solid`（纯色经典零透光）。
  - **氛围色板**：默认翡翠绿、米黄纸色（暖琥珀）、纯黑（极夜天际冷蓝）、青墨（苍松黛青）、浅紫（暮色紫罗兰）、复古暖褐（古籍焦糖），支持 0%~90% 底色浓度自由微调。
- **🛡️ 本地优先与三重数据防丢机制**：
  - 所有日记均以标准 UTF-8 Markdown 文件（`yyyy-MM-dd.md`）存储在本地文档目录（`Documents\JournalData\Diaries`），配合 SQLite 高速索引实现毫秒级检索；
  - **原子写入**：通过 `.tmp` 临时文件原子替换落盘，杜绝断电损坏；
  - **自动历史快照（Backups）**：每次覆盖修改已有日记前自动备份历史版本；
  - **安全回收站（Trash）**：删除操作默认移入 `Trash/` 回收站目录，防止误删丢失。

---

## 📦 下载与安装 (Installation)

前往 [**Releases 页面**](https://github.com/met-tk/Jot/releases/latest) 下载最新安装包 **`Jot_Setup_v1.4.0.exe`**：
- 内置独立 .NET 8 与 Windows App SDK 运行时，**无需额外安装任何环境**，双击即可完成安装与桌面快捷方式创建。

---

## ⌨️ 常用快捷键 (Shortcuts)

| 快捷键 | 功能说明 |
| :--- | :--- |
| `Ctrl + 1 ~ 6` | 快速切换：今日随记 / 日历透视 / 目录时间线 / 数据统计 / 导入工作台 / 设置中心 |
| `Ctrl + T` | 在当前光标处插入 `[HH:mm:ss]` 时间戳 |
| `Ctrl + S` | 立即保存当前日记 |
| `Ctrl + Z` / `Ctrl + Shift + Z` | 撤回 / 重做 |
| `鼠标侧键 (前进 / 后退)` | 跨视图层级返回或历史页面前进/后退 |

---
