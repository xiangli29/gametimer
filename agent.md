# GameLauncherPro 项目功能总结

## 项目定位

GameLauncherPro 是一个基于 .NET 8 WPF 的 Windows 桌面应用，窗口标题为“游戏时长统计助手 | 典藏版”。它的核心目标是帮助用户管理本机游戏库、启动游戏、自动监控游戏进程，并统计每个游戏的累计游玩时长、最近游玩时间、封面、评分等信息。

## 技术栈

- 应用类型：WPF 桌面程序
- 目标框架：`net8.0-windows`
- UI 技术：WPF XAML
- 图表库：`LiveChartsCore.SkiaSharpView.WPF`
- 文件选择：`Microsoft.Win32.OpenFileDialog`
- 文件夹选择和电源状态：`System.Windows.Forms`
- 数据存储：JSON 文件

## 主要功能

### 1. 游戏库管理

主界面提供“我的游戏库”区域，用卡片形式展示已记录的游戏。每张卡片包含游戏名、评分、封面图，以及以下操作：

- 点击游戏名启动游戏。
- 设置正面封面。
- 设置反面封面。
- 翻转卡片，在正面和反面封面之间切换。
- 设置 0 到 10 分的个人评分。

游戏卡片通过 `ObservableCollection<GameViewModel>` 绑定到界面，使用自定义 `VirtualizingWrapPanel` 实现固定尺寸卡片的虚拟化排列，减少大量游戏卡片时的界面开销。

### 2. 游戏启动

每个游戏数据中保存 `launch_exe` 启动路径。用户点击游戏名后，程序使用 `ProcessStartInfo` 和 `UseShellExecute = true` 启动对应 exe。如果启动文件不存在，会弹出错误提示。

### 3. 运行中游戏识别

用户可以先选择一个游戏总文件夹。程序会在该目录下识别运行中的 exe：

- 使用 `DispatcherTimer` 每隔一定时间触发监控。
- 遍历系统进程，跳过当前程序自身。
- 只统计有主窗口标题的进程。
- 读取进程主模块路径，并判断 exe 是否位于用户设置的游戏目录下。
- 根据 exe 所在目录名推断游戏名。
- 记录运行开始时间，并在“实时运行中游戏”面板显示当前会话已游玩时长。

当某个游戏进程停止后，程序会计算本次运行持续时间，累加到该游戏的 `total_seconds`，更新 `last_play`，并保存 exe 路径。

### 4. 手动加入游戏库

程序当前不再自动把目录下所有游戏加入库，而是通过“将当前游戏加入库”按钮，把当前识别到的运行中游戏加入游戏库。加入时会保存：

- 游戏名
- 启动 exe 路径
- exe 路径列表

### 5. 时长统计和历史记录

界面提供“历史游玩记录”区域，显示：

- 当前统计时间
- 当前监控目录
- 每个游戏的总时长
- 最后游玩时间
- 启动路径

时长格式为 `00小时00分钟00秒`。

### 6. 图表和排行榜

应用使用 LiveCharts 显示游戏时长统计：

- 左侧柱状图：展示各游戏累计时长。
- 中间饼图：展示各游戏时长占比。
- 右侧排行榜：按累计时长从高到低列出游戏，并显示总时长。

图表支持刷新策略配置：

- 手动刷新
- 插电时自动刷新
- 始终自动刷新
- 强省电模式

在电池模式下，监控间隔会自动拉长，图表刷新也会受到限制以降低电量消耗。

### 7. 搜索和排序

游戏库支持搜索框过滤游戏名，并支持下拉排序：

- 默认排序
- 按游玩时间从大到小
- 按最近打开从近到远
- 按评分从高到低

搜索输入带有 300ms 防抖，避免输入过程中频繁重建界面。

### 8. 数据持久化

应用数据保存到用户 AppData 目录：

- 配置文件：`%APPDATA%\GameLauncherPro\config.json`
- 游戏数据：`%APPDATA%\GameLauncherPro\game_play_time.json`
- 缩略图目录：`%APPDATA%\GameLauncherPro\thumbnails`

配置文件保存：

- 游戏监控目录
- 图表自动刷新模式
- 强省电模式

游戏数据保存：

- 累计游玩秒数
- 最后游玩时间
- exe 路径列表
- 正面封面路径
- 反面封面路径
- 当前显示面
- 用户评分
- 启动 exe 路径

保存游戏数据时会先写入临时文件，再替换正式数据文件，降低写入中断导致数据损坏的概率。保存操作带 1 秒防抖，并会写入 `save_log.txt` 日志。

### 9. 图片加载和缩略图

封面图片支持异步加载，并限制并发数量。加载时会设置解码尺寸以降低内存压力。项目中还设计了缩略图路径生成逻辑，使用原图路径的 MD5 生成稳定文件名，并保存为 jpg 缩略图。

## 关键文件说明

- `GameLauncherPro.slnx`：解决方案文件，引用主 WPF 项目。
- `GameLauncherPro/GameLauncherPro.csproj`：项目文件，声明 .NET 8 WPF、Windows Forms 和 LiveCharts 依赖。
- `GameLauncherPro/App.xaml`：应用入口，启动 `MainWindow.xaml`。
- `GameLauncherPro/MainWindow.xaml`：主界面布局，包含游戏库、运行中游戏、历史记录、图表和排行榜。
- `GameLauncherPro/MainWindow.xaml.cs`：主业务逻辑，包括配置读写、数据读写、进程监控、游戏启动、封面设置、评分、搜索排序、图表刷新。
- `GameLauncherPro/GameData.cs`：游戏数据模型。
- `GameLauncherPro/ViewModels/GameViewModel.cs`：游戏卡片视图模型，负责界面绑定字段和属性变更通知。
- `GameLauncherPro/Controls/VirtualizingWrapPanel.cs`：自定义虚拟化面板，用于游戏卡片瀑布/换行布局。
- `GameLauncherPro/Themes/Styles.xaml`：主题资源和控件样式。

## 当前代码状态观察

- 项目包含大量 `bin`、`obj` 和发布目录产物，源码主要集中在 `GameLauncherPro` 目录下。
- 部分中文注释或字符串在源码中出现乱码，可能是文件编码曾经不一致导致。
- `MainWindow.xaml.cs` 承担了大部分业务逻辑，后续如果继续扩展，可以考虑逐步拆分为配置服务、数据仓储、进程监控服务和图表刷新服务。
- `RenderGameLibrary` 中“按最近打开”判断字符串和 XAML 下拉项存在一个括号文本不一致的问题，可能导致该排序分支无法命中。
