# GameLauncherPro 典藏版

一个基于 .NET 8 WPF 的 Windows 桌面游戏时长统计工具。自动监控游戏进程，记录每个游戏的累计游玩时间、最近游玩时间，并以可视化图表和排行榜展示统计数据。

## 功能

- **自动进程监控** — 设置游戏总文件夹后，自动识别其中运行的游戏进程，实时显示当前游玩时长
- **游戏库管理** — 将识别的游戏加入库，自定义封面（正/反面）、评分、重命名，支持搜索和多种排序
- **卡片翻转** — 支持正面/反面封面翻转动画，类似实体卡牌收藏体验
- **游戏启动** — 点击卡片上的游戏名称直接启动对应 exe
- **时长统计** — 自动累计游戏运行时间，支持手动修改时长
- **图表可视化** — LiveCharts 柱状图、饼图和排行榜，展示各游戏时长占比
- **历史记录** — 实时监控面板和历史记录面板，记录所有游戏的累计时长、最后游玩时间、启动路径
- **电源感知** — 插电/电池模式自动调整监控频率和图表刷新策略，强省电模式下最大限度降低能耗
- **系统托盘** — 最小化到系统托盘，后台静默监控

## 技术栈

| 层面 | 技术 |
|------|------|
| 框架 | .NET 8.0 (WPF + Windows Forms) |
| 图表 | LiveChartsCore.SkiaSharpView.WPF 2.0 |
| 数据 | JSON 文件存储 (System.Text.Json) |
| 架构 | MVVM (GameViewModel) + Service 分层 |

## 快速开始

### 环境要求

- Windows 10 / 11
- .NET 8.0 SDK
- Visual Studio 2022 或 JetBrains Rider

### 构建

```bash
dotnet build GameLauncherPro/GameLauncherPro.csproj -c Release
```

### 运行

```bash
dotnet run --project GameLauncherPro/GameLauncherPro.csproj
```

或直接双击 `GameLauncherPro/bin/Release/net8.0-windows/GameLauncherPro.exe`。

## 数据存储

所有数据存放在 `%APPDATA%\GameLauncherPro\`：

| 文件 | 说明 |
|------|------|
| `config.json` | 配置：监控目录、图表刷新模式、强省电开关 |
| `game_play_time.json` | 游戏库数据：时长、评分、封面路径、启动 exe |
| `thumbnails/` | 封面缩略图缓存（JPEG，按原路径 MD5 命名） |

## 项目结构

```
GameLauncherPro/
├── App.xaml / .cs                 # 应用入口
├── MainWindow.xaml / .cs          # 主窗口 + UI 事件处理
├── GameData.cs                    # 游戏数据模型
├── Controls/
│   └── VirtualizingWrapPanel.cs   # 自定义虚拟化换行面板
├── Services/
│   ├── GameDataService.cs         # JSON 配置/游戏数据读写
│   ├── ProcessMonitorService.cs   # 进程监控与游戏识别
│   ├── GameLibraryController.cs   # 游戏库增删改查 + 排序过滤
│   ├── ChartService.cs            # 图表数据生成与排行榜
│   └── ImageCacheService.cs       # 封面异步加载 + 内存/磁盘缓存
├── ViewModels/
│   └── GameViewModel.cs           # 游戏卡片视图模型
└── Themes/
    └── Styles.xaml                # 深色轻奢暖金风格主题
```

## 使用流程

1. 点击 **「切换游戏文件夹」**，选择存放游戏的根目录
2. 运行你的游戏，程序会自动在「实时运行」面板中识别
3. 点击 **「将当前游戏加入库」**，游戏即出现在游戏库中
4. 在卡片上设置封面（F = 正面 / B = 背面）、评分、时长等
5. 点击游戏名称即可启动游戏；点击翻转按钮切换正反面封面
6. 在「游戏时长统计」区域查看柱状图、饼图和排行榜

## 图表刷新策略

| 模式 | 行为 |
|------|------|
| 手动 | 仅在手动刷新时更新图表 |
| 插电 | 插电时自动刷新，电池时仅更新排行榜 |
| 始终 | 始终自动刷新图表（电池下每 2 分钟刷新） |
| 强省电 | 电池下完全禁用图表自动刷新 |

## 许可

MIT License

---

*GameLauncherPro 典藏版 — 你的游戏收藏，每一分钟的陪伴都值得记录。*
