using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncherPro.Services;
using GameLauncherPro.ViewModels;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MediaColor = System.Windows.Media.Color;

namespace GameLauncherPro
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly GameDataService _data = new();
        private readonly ChartService _chartService = new();
        private readonly ImageCacheService _imageCache;
        private readonly GameLibraryController _library;

        private readonly TimeSpan _chartUpdateIntervalOnBattery = TimeSpan.FromMinutes(2);

        private ProcessMonitorService _monitor = null!;
        private WinForms.NotifyIcon? _trayIcon;
        private System.Drawing.Icon? _trayIconImage;
        private DispatcherTimer? _searchDebounceTimer;
        private DispatcherTimer? _saveDebounceTimer;
        private DateTime _lastChartUpdateTime = DateTime.MinValue;

        private CartesianChart? _barChart;
        private PieChart? _pieChart;
        private GameViewModel? _selectedGame;
        private int _screenshotIndex;

        public MainWindow()
        {
            InitializeComponent();

            _data.LoadConfig();
            ThemeService.Apply(this, _data.DarkMode);
            _data.LoadGameData();
            _data.SaveConfig();

            _imageCache = new ImageCacheService(_data.GetAppDataDir());
            _library = new GameLibraryController(_data, _imageCache);

            DataContext = this;
            RefreshTagCatalog();
            InitializeTimers();
            InitializeCharts();
            InitializeProcessMonitor();
            InitializeTrayIcon();

            ApplyConfigToUI();
            ApplyLibraryViewState();
            ShowPage("library");
            _ = ReloadDataViewsAsync(includeCharts: true);

            Loaded += (_, _) => HookNestedScrolling(this);
            Closed += (_, _) => CleanupResources();
        }

        public System.ComponentModel.ICollectionView GamesView => _library.GamesView;

        public ObservableCollection<TagDisplayViewModel> TagCatalog { get; } = new();

        public ObservableCollection<TagOptionViewModel> TagOptions { get; } = new();

        public GameViewModel? SelectedGame
        {
            get => _selectedGame;
            private set
            {
                if (ReferenceEquals(_selectedGame, value))
                {
                    return;
                }

                _selectedGame = value;
                _screenshotIndex = 0;
                RaisePropertyChanged(nameof(SelectedGame));
                RaisePropertyChanged(nameof(HasSelectedGame));
                RaiseScreenshotNavigationProperties();
            }
        }

        public bool HasSelectedGame => SelectedGame is not null;

        public ScreenshotViewModel? CurrentScreenshot
        {
            get
            {
                if (SelectedGame is null || SelectedGame.Screenshots.Count == 0)
                {
                    return null;
                }

                var index = Math.Clamp(_screenshotIndex, 0, SelectedGame.Screenshots.Count - 1);
                return SelectedGame.Screenshots[index];
            }
        }

        public bool HasScreenshots => CurrentScreenshot is not null;

        public bool CanShowPreviousScreenshot => SelectedGame is not null && _screenshotIndex > 0;

        public bool CanShowNextScreenshot =>
            SelectedGame is not null && _screenshotIndex < SelectedGame.Screenshots.Count - 1;

        public string ScreenshotPositionText =>
            SelectedGame is null || SelectedGame.Screenshots.Count == 0
                ? "暂无截图"
                : $"{_screenshotIndex + 1} / {SelectedGame.Screenshots.Count}";

        private void RaisePropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private void RaiseScreenshotNavigationProperties()
        {
            RaisePropertyChanged(nameof(CurrentScreenshot));
            RaisePropertyChanged(nameof(HasScreenshots));
            RaisePropertyChanged(nameof(CanShowPreviousScreenshot));
            RaisePropertyChanged(nameof(CanShowNextScreenshot));
            RaisePropertyChanged(nameof(ScreenshotPositionText));
        }

        private void InitializeTimers()
        {
            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounceTimer.Tick += async (_, _) =>
            {
                _searchDebounceTimer.Stop();
                ApplyLibraryViewState();
                await Task.CompletedTask;
            };

            _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveDebounceTimer.Tick += (_, _) =>
            {
                _saveDebounceTimer.Stop();
                _data.SaveGameData();
            };
        }

        private void InitializeProcessMonitor()
        {
            _monitor = new ProcessMonitorService(
                _data,
                hasChanges => Dispatcher.InvokeAsync(() => HandleMonitorTickAsync(hasChanges), DispatcherPriority.Background),
                ScheduleSaveGameData);

            _monitor.RunningStateUpdated += (displayText, hasRunning) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    Tb_Running.Text = displayText;
                    RunningIndicator.Fill = hasRunning
                        ? ThemeService.GetBrush("RunningActiveBrush") ?? new SolidColorBrush(MediaColor.FromRgb(0x3F, 0xB9, 0x50))
                        : ThemeService.GetBrush("RunningIdleBrush") ?? new SolidColorBrush(MediaColor.FromRgb(0x48, 0x4F, 0x58));
                }, DispatcherPriority.Background);
            };

            _monitor.Start();
            if (!string.IsNullOrWhiteSpace(_data.GameRootDir))
            {
                _monitor.ScanAllGameExes();
            }
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon
            {
                Text = "GameLauncherPro",
                Visible = false
            };

            try
            {
                var trayIconPath = Path.Combine(AppContext.BaseDirectory, "tray.ico");
                if (File.Exists(trayIconPath))
                {
                    _trayIconImage = new System.Drawing.Icon(trayIconPath);
                    _trayIcon.Icon = _trayIconImage;
                }
                else
                {
                    var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executablePath))
                    {
                        _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
                    }
                }
            }
            catch
            {
            }

            _trayIcon.DoubleClick += (_, _) => ShowFromTray();

            var trayMenu = new WinForms.ContextMenuStrip();
            trayMenu.Items.Add("显示", null, (_, _) => ShowFromTray());
            trayMenu.Items.Add("退出", null, (_, _) =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }

                System.Windows.Application.Current.Shutdown();
            });

            _trayIcon.ContextMenuStrip = trayMenu;
        }

        private void InitializeCharts()
        {
            _barChart = FindName(nameof(Lc_BarChart)) as CartesianChart;
            _pieChart = FindName(nameof(Lc_PieChart)) as PieChart;

            if (_barChart is not null)
            {
                _barChart.Series = Array.Empty<ISeries>();
                _barChart.XAxes = new[] { new Axis() };
                _barChart.YAxes = new[] { new Axis() };
            }

            if (_pieChart is not null)
            {
                _pieChart.Series = Array.Empty<ISeries>();
            }
        }

        private async Task ReloadDataViewsAsync(bool includeCharts)
        {
            ApplyLibraryViewState();
            await _library.RefreshAsync();
            if (SelectedGame is not null)
            {
                RefreshTagOptions();
            }
            RenderRecord();
            UpdateStatusBar();
            if (includeCharts)
            {
                RefreshCharts();
            }
        }

        private async void HandleMonitorTickAsync(bool hasChanges)
        {
            if (hasChanges)
            {
                await ReloadDataViewsAsync(includeCharts: false);
                RefreshChartsPowerAware();
                return;
            }

            RenderRecord();
            UpdateStatusBar();
            RefreshChartsPowerAware();
        }

        private void ApplyLibraryViewState()
        {
            _library.ApplyViewState(Tb_Search.Text, GetSelectedSortOption(), GetSelectedStatusFilter());
        }

        private void ScheduleSaveGameData()
        {
            _saveDebounceTimer?.Stop();
            _saveDebounceTimer?.Start();
        }

        private void ApplyConfigToUI()
        {
            Rb_Manual.IsChecked = _data.AutoRefreshMode == AutoRefreshModeEnum.Manual;
            Rb_AutoOnAC.IsChecked = _data.AutoRefreshMode == AutoRefreshModeEnum.AutoOnAC;
            Rb_Always.IsChecked = _data.AutoRefreshMode == AutoRefreshModeEnum.Always;
            Cb_StrongPower.IsChecked = _data.StrongPowerSaving;
            Tg_DarkMode.IsChecked = _data.DarkMode;
            UpdateCheckInDisplay();

            _data.AutoRefreshCharts = _data.AutoRefreshMode == AutoRefreshModeEnum.Always
                || (_data.AutoRefreshMode == AutoRefreshModeEnum.AutoOnAC && ProcessMonitorService.IsOnACPower());
        }

        private GameSortOption GetSelectedSortOption()
        {
            return Cb_Filter.SelectedIndex switch
            {
                1 => GameSortOption.PlayTimeDesc,
                2 => GameSortOption.LastPlayedDesc,
                3 => GameSortOption.ScoreDesc,
                _ => GameSortOption.Default
            };
        }

        private void RefreshChartsPowerAware()
        {
            var shouldRefreshCharts = _data.AutoRefreshMode switch
            {
                AutoRefreshModeEnum.Always => true,
                AutoRefreshModeEnum.AutoOnAC => ProcessMonitorService.IsOnACPower(),
                _ => false
            };

            if (_data.StrongPowerSaving && !ProcessMonitorService.IsOnACPower())
            {
                shouldRefreshCharts = false;
            }

            if (!shouldRefreshCharts)
            {
                DrawRankOnly();
                return;
            }

            if (ProcessMonitorService.IsOnACPower() || DateTime.Now - _lastChartUpdateTime > _chartUpdateIntervalOnBattery)
            {
                RefreshCharts();
                _lastChartUpdateTime = DateTime.Now;
                return;
            }

            DrawRankOnly();
        }

        private void DrawRankOnly()
        {
            var snapshot = _chartService.BuildSnapshot(_data.GetSnapshot());
            Tb_Rank.Text = snapshot.RankText;
        }

        private void RefreshCharts()
        {
            if (_barChart is null || _pieChart is null)
            {
                return;
            }

            var snapshot = _chartService.BuildSnapshot(_data.GetSnapshot());
            Tb_Rank.Text = snapshot.RankText;

            var labelColor = GetThemeSkColor("ChartAxisBrush", 0x64, 0x74, 0x8B);
            _barChart.XAxes = new[]
            {
                new Axis
                {
                    Labels = snapshot.Labels,
                    LabelsRotation = 45,
                    LabelsPaint = new SolidColorPaint(labelColor)
                }
            };
            _barChart.YAxes = new[]
            {
                new Axis
                {
                    Labeler = value => _chartService.FormatTime((int)value),
                    LabelsPaint = new SolidColorPaint(labelColor)
                }
            };
            _barChart.Series = new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Values = snapshot.Values,
                    Fill = new SolidColorPaint(GetThemeSkColor("AccentBrush", 0x3B, 0x82, 0xF6)),
                    Stroke = new SolidColorPaint(GetThemeSkColor("AccentHoverBrush", 0x25, 0x63, 0xEB)) { StrokeThickness = 1 }
                }
            };

            var pieColors = new[]
            {
                new SkiaSharp.SKColor(0x3B, 0x82, 0xF6),
                new SkiaSharp.SKColor(0x22, 0xC5, 0x5E),
                new SkiaSharp.SKColor(0xA7, 0x8B, 0xFA),
                new SkiaSharp.SKColor(0xF9, 0x73, 0x16),
                new SkiaSharp.SKColor(0x06, 0xB6, 0xD4),
                new SkiaSharp.SKColor(0xE0, 0x52, 0x52),
                new SkiaSharp.SKColor(0xEA, 0xB3, 0x08),
                new SkiaSharp.SKColor(0xEC, 0x48, 0x99),
                new SkiaSharp.SKColor(0x14, 0xB8, 0xA6),
                new SkiaSharp.SKColor(0x84, 0xCC, 0x16)
            };

            var pieSeries = new List<ISeries>(snapshot.OrderedGames.Count);
            for (var index = 0; index < snapshot.OrderedGames.Count; index++)
            {
                var item = snapshot.OrderedGames[index];
                pieSeries.Add(new PieSeries<double>
                {
                    Values = new[] { (double)item.Value.total_seconds },
                    Name = item.Key,
                    Fill = new SolidColorPaint(pieColors[index % pieColors.Length]),
                    Stroke = new SolidColorPaint(GetThemeSkColor("PanelRaisedBrush", 0xE9, 0xED, 0xF1)) { StrokeThickness = 1 }
                });
            }

            _pieChart.Series = pieSeries.ToArray();
        }

        private string GetSelectedStatusFilter() =>
            Cb_StatusFilter.SelectedItem is ComboBoxItem { Content: string status }
                && GameDataService.GameStatuses.Contains(status, StringComparer.Ordinal)
                ? status
                : string.Empty;

        private static SkiaSharp.SKColor GetThemeSkColor(string resourceKey, byte fallbackRed, byte fallbackGreen, byte fallbackBlue)
        {
            if (ThemeService.GetBrush(resourceKey) is SolidColorBrush brush)
            {
                return new SkiaSharp.SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
            }

            return new SkiaSharp.SKColor(fallbackRed, fallbackGreen, fallbackBlue);
        }

        private void RenderRecord()
        {
            var snapshot = _data.GetSnapshot();
            Tb_Record.Text = _chartService.BuildRecordText(_data.GameRootDir, snapshot);
        }

        private void UpdateStatusBar()
        {
            var snapshot = _data.GetSnapshot();
            var totalSeconds = snapshot.Values.Sum(game => game.total_seconds);
            Tb_StatusDir.Text = string.IsNullOrWhiteSpace(_data.GameRootDir)
                ? "未设置监控目录"
                : $"监控目录: {_data.GameRootDir}";
            Tb_StatusNum.Text = $"共 {snapshot.Count} 款游戏";
            Tb_StatusTotal.Text = $"总时长: {_chartService.FormatTime(totalSeconds)}";
            Tb_GameCount.Text = snapshot.Count > 0 ? $"({snapshot.Count} 款)" : "";
            Tb_MonitorDirectory.Text = string.IsNullOrWhiteSpace(_data.GameRootDir)
                ? "尚未选择游戏文件夹"
                : _data.GameRootDir;

            Tb_OverviewGameCount.Text = snapshot.Count.ToString();
            Tb_OverviewTotalTime.Text = _chartService.FormatTime(totalSeconds);
            var recent = snapshot
                .Where(game => !string.IsNullOrWhiteSpace(game.Value.last_play))
                .OrderByDescending(game => game.Value.last_play)
                .FirstOrDefault();
            Tb_OverviewRecentGame.Text = string.IsNullOrWhiteSpace(recent.Key)
                ? "暂无游玩记录"
                : recent.Key;
        }

        private void UpdateCheckInDisplay()
        {
            var count = Math.Max(0, _data.CheckInCount);
            Tb_CheckInCount.Text = $"累计 {count} 次";
            Tb_CheckInCountSetting.Text = count.ToString();
        }

        private void RefreshTagCatalog()
        {
            TagCatalog.Clear();
            foreach (var tag in _data.GetTagCatalogSnapshot())
            {
                TagCatalog.Add(new TagDisplayViewModel(tag));
            }

            RefreshTagOptions();
        }

        private void RefreshTagOptions()
        {
            TagOptions.Clear();
            if (SelectedGame is null)
            {
                return;
            }

            var assignedTags = new HashSet<string>(SelectedGame.Tags, StringComparer.OrdinalIgnoreCase);
            foreach (var tag in TagCatalog)
            {
                TagOptions.Add(new TagOptionViewModel(
                    new TagDefinition { name = tag.Name, color = tag.Color },
                    assignedTags.Contains(tag.Name)));
            }
        }

        private void NavLibrary_Click(object sender, RoutedEventArgs e) => ShowPage("library");

        private void NavOverview_Click(object sender, RoutedEventArgs e) => ShowPage("overview");

        private void NavMonitor_Click(object sender, RoutedEventArgs e) => ShowPage("monitor");

        private void ShowPage(string page)
        {
            LibraryPage.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
            OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
            MonitorPage.Visibility = page == "monitor" ? Visibility.Visible : Visibility.Collapsed;

            var selectedBackground = (System.Windows.Media.Brush)FindResource("NavSelectedBrush");
            var selectedForeground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
            var defaultForeground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");

            Btn_NavLibrary.Background = page == "library" ? selectedBackground : System.Windows.Media.Brushes.Transparent;
            Btn_NavOverview.Background = page == "overview" ? selectedBackground : System.Windows.Media.Brushes.Transparent;
            Btn_NavMonitor.Background = page == "monitor" ? selectedBackground : System.Windows.Media.Brushes.Transparent;
            Btn_NavLibrary.Foreground = page == "library" ? selectedForeground : defaultForeground;
            Btn_NavOverview.Foreground = page == "overview" ? selectedForeground : defaultForeground;
            Btn_NavMonitor.Foreground = page == "monitor" ? selectedForeground : defaultForeground;
        }

        private async void Tb_Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_searchDebounceTimer is null)
            {
                ApplyLibraryViewState();
                return;
            }

            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
            await Task.CompletedTask;
        }

        private void Cb_Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            ApplyLibraryViewState();
        }

        private void Cb_StatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            ApplyLibraryViewState();
        }

        private void Rb_AutoManual_Checked(object sender, RoutedEventArgs e)
        {
            _data.AutoRefreshMode = AutoRefreshModeEnum.Manual;
            ApplyConfigToUI();
            _data.SaveConfig();
        }

        private void Rb_AutoOnAC_Checked(object sender, RoutedEventArgs e)
        {
            _data.AutoRefreshMode = AutoRefreshModeEnum.AutoOnAC;
            ApplyConfigToUI();
            _data.SaveConfig();
        }

        private void Rb_AutoAlways_Checked(object sender, RoutedEventArgs e)
        {
            _data.AutoRefreshMode = AutoRefreshModeEnum.Always;
            ApplyConfigToUI();
            _data.SaveConfig();
        }

        private void Cb_StrongPower_Checked(object sender, RoutedEventArgs e)
        {
            _data.StrongPowerSaving = Cb_StrongPower.IsChecked == true;
            _data.SaveConfig();
        }

        private void Tg_DarkMode_Changed(object sender, RoutedEventArgs e)
        {
            _data.DarkMode = Tg_DarkMode.IsChecked == true;
            ThemeService.Apply(this, _data.DarkMode);
            _library.RefreshTheme();
            RefreshTagCatalog();
            RefreshCharts();

            if (IsLoaded)
            {
                _data.SaveConfig();
            }
        }

        private void Btn_CheckIn_Click(object sender, RoutedEventArgs e)
        {
            _data.CheckInCount = Math.Max(0, _data.CheckInCount) + 1;
            _data.SaveConfig();
            UpdateCheckInDisplay();
        }

        private void Btn_SaveCheckInCount_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(Tb_CheckInCountSetting.Text.Trim(), out var count) || count < 0)
            {
                MessageBox.Show("请输入非负整数。", "打卡次数格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _data.CheckInCount = count;
            _data.SaveConfig();
            UpdateCheckInDisplay();
        }

        private void Btn_AddCatalogTag_Click(object sender, RoutedEventArgs e)
        {
            if (_data.AddTagToCatalog(Tb_NewCatalogTag.Text, out _))
            {
                _data.SaveConfig();
                RefreshTagCatalog();
            }

            Tb_NewCatalogTag.Clear();
        }

        private async void Btn_DeleteCatalogTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag })
            {
                return;
            }

            var result = MessageBox.Show(
                $"删除标签“{tag}”会同步从所有游戏中移除它，是否继续？",
                "确认删除标签",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes || !_data.RemoveTagFromCatalog(tag))
            {
                return;
            }

            _data.SaveConfig();
            _data.SaveGameData();
            RefreshTagCatalog();
            await ReloadDataViewsAsync(includeCharts: false);
        }

        private async void Btn_ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "请选择游戏总文件夹",
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                ShowNewFolderButton = true
            };

            var result = dialog.ShowDialog();
            if (result != WinForms.DialogResult.OK && result != WinForms.DialogResult.Yes)
            {
                return;
            }

            _data.GameRootDir = dialog.SelectedPath;
            _data.SaveConfig();
            _monitor.CachedExes.Clear();
            await Task.Run(() => _monitor.ScanAllGameExes());
            RenderRecord();
            UpdateStatusBar();
            MessageBox.Show("已设置监控目录。请运行游戏后再点击“将当前游戏加入库”。", "提示");
        }

        private async void Btn_AddGame_Click(object sender, RoutedEventArgs e)
        {
            if (_monitor.RunningGameExePaths.Count == 0)
            {
                MessageBox.Show("请先运行游戏，程序会自动识别运行中的游戏。", "提示");
                return;
            }

            var firstGame = _monitor.RunningGameExePaths.First();
            _library.AddOrUpdateGame(firstGame.Key, firstGame.Value);
            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: true);
            MessageBox.Show($"已将游戏 {firstGame.Key} 添加到库中。", "成功");
        }

        private async void Btn_Refresh_Click(object sender, RoutedEventArgs e)
        {
            _data.LoadGameData();
            await ReloadDataViewsAsync(includeCharts: true);
            MessageBox.Show("数据已刷新。若要新增游戏，请使用“将当前游戏加入库”。", "提示");
        }

        private void Btn_FlipAll_Click(object sender, RoutedEventArgs e)
        {
            _library.ToggleAllCurrentSides();
            _data.SaveGameData();
        }

        private void Btn_ExportJson_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON 文件|*.json",
                FileName = $"game_play_time_{DateTime.Now:yyyyMMdd}.json"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var content = JsonSerializer.Serialize(
                    _data.GetSnapshot(),
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dialog.FileName, content);
                MessageBox.Show("记录已导出。", "导出完成");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Btn_ImportJson_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON 文件|*.json"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            Dictionary<string, GameData>? imported;
            try
            {
                imported = JsonSerializer.Deserialize<Dictionary<string, GameData>>(File.ReadAllText(dialog.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法读取 JSON：{ex.Message}", "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (imported is null)
            {
                MessageBox.Show("JSON 中没有有效的游戏记录。", "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                "导入会完全替换当前游戏记录，是否继续？",
                "确认导入",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var game in imported.Values)
            {
                game.exe_paths ??= new List<string>();
                game.screenshot_paths ??= new List<string>();
                game.cover_path ??= string.Empty;
                game.cover_back_path ??= string.Empty;
                game.current_side ??= "front";
                game.last_play ??= string.Empty;
                game.launch_exe ??= string.Empty;
                game.status = GameDataService.NormalizeStatus(game.status);
                game.tags = GameDataService.NormalizeTags(game.tags);
                game.review ??= string.Empty;
            }

            CloseDrawer();
            _data.ReplaceGameData(imported);
            _data.SaveGameData();
            _data.SaveConfig();
            RefreshTagCatalog();
            await ReloadDataViewsAsync(includeCharts: true);
            MessageBox.Show($"已导入 {imported.Count} 条游戏记录。", "导入完成");
        }

        private void CardMore_Click(object sender, RoutedEventArgs e)
        {
            if (TryGetCardViewModel(sender, out var game))
            {
                OpenDrawer(game);
            }
        }

        private void Btn_CloseDrawer_Click(object sender, RoutedEventArgs e)
        {
            CloseDrawer();
        }

        private void DrawerBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CloseDrawer();
        }

        private async void ScoreOption_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCardViewModel(sender, out var game)
                || sender is not System.Windows.Controls.Button button
                || !int.TryParse(button.Tag?.ToString(), out var score))
            {
                return;
            }

            _library.SetScore(game.Name, Math.Clamp(score, 0, 10));
            ScheduleSaveGameData();
            ApplyLibraryViewState();
            await ReloadDataViewsAsync(includeCharts: true);
        }

        private void Cb_DrawerStatus_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedGame is null
                || sender is not System.Windows.Controls.ComboBox { SelectedItem: ComboBoxItem { Content: string status } }
                || string.Equals(SelectedGame.Status, status, StringComparison.Ordinal))
            {
                return;
            }

            _library.SetStatus(SelectedGame.Name, status);
            _data.SaveGameData();
            ApplyLibraryViewState();
        }

        private void TagOption_Changed(object sender, RoutedEventArgs e)
        {
            if (SelectedGame is null
                || sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string tag } toggle)
            {
                return;
            }

            var isAssigned = SelectedGame.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
            var shouldBeAssigned = toggle.IsChecked == true;
            if (isAssigned == shouldBeAssigned)
            {
                return;
            }

            _library.ToggleTag(SelectedGame.Name, tag);
            _data.SaveGameData();
            ApplyLibraryViewState();
        }

        private void Btn_AddDrawerTag_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGame is null)
            {
                return;
            }

            _data.AddTagToCatalog(Tb_NewDrawerTag.Text, out var tag);
            if (string.IsNullOrWhiteSpace(tag.name))
            {
                return;
            }

            if (!SelectedGame.Tags.Contains(tag.name, StringComparer.OrdinalIgnoreCase))
            {
                _library.ToggleTag(SelectedGame.Name, tag.name);
                _data.SaveGameData();
            }

            _data.SaveConfig();
            Tb_NewDrawerTag.Clear();
            RefreshTagCatalog();
            ApplyLibraryViewState();
        }

        private void Btn_SaveDrawerReview_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGame is null)
            {
                return;
            }

            _library.SetReview(SelectedGame.Name, Tb_DrawerReview.Text);
            _data.SaveGameData();
        }

        private async void Btn_SaveDrawerTime_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGame is null)
            {
                return;
            }

            if (!int.TryParse(Tb_DrawerHours.Text.Trim(), out var hours)
                || !int.TryParse(Tb_DrawerMinutes.Text.Trim(), out var minutes)
                || hours < 0
                || minutes < 0
                || minutes > 59)
            {
                MessageBox.Show("请输入有效的小时和分钟（分钟范围为 0-59）。", "时长格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _library.SetTotalSeconds(SelectedGame.Name, hours * 3600 + minutes * 60, DateTime.Now);
            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: true);
            OpenDrawer(SelectedGame);
        }

        private async void Btn_SaveDrawerName_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGame is null)
            {
                return;
            }

            var oldName = SelectedGame.Name;
            var newName = Tb_DrawerName.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!_library.RenameGame(oldName, newName))
            {
                MessageBox.Show($"游戏 \"{newName}\" 已存在，请使用其他名称。", "重命名失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CloseDrawer();
            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: true);
        }

        private void OpenDrawer(GameViewModel game)
        {
            SelectedGame = game;
            _screenshotIndex = 0;
            RaiseScreenshotNavigationProperties();
            Tb_DrawerName.Text = game.Name;
            Tb_DrawerHours.Text = (game.TotalSeconds / 3600).ToString();
            Tb_DrawerMinutes.Text = ((game.TotalSeconds % 3600) / 60).ToString("D2");
            Tb_DrawerReview.Text = game.Review;
            Tb_NewDrawerTag.Clear();
            Cb_DrawerStatus.SelectedItem = Cb_DrawerStatus.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Content?.ToString(), game.Status, StringComparison.Ordinal));
            RefreshTagOptions();
        }

        private void CloseDrawer()
        {
            SelectedGame = null;
            TagOptions.Clear();
        }

        private void CardFlip_Click(object sender, RoutedEventArgs e)
        {
            if (TryGetCardViewModel(sender, out var game))
            {
                _library.ToggleCurrentSide(game.Name);
                ScheduleSaveGameData();
            }
        }

        private async void CardSetFront_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCardViewModel(sender, out var game))
            {
                return;
            }

            var file = PromptForImageFile();
            if (file is null)
            {
                return;
            }

            _library.SetCover(game.Name, file, setFront: true);
            ScheduleSaveGameData();
            await ReloadDataViewsAsync(includeCharts: false);
        }

        private async void CardSetBack_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCardViewModel(sender, out var game))
            {
                return;
            }

            var file = PromptForImageFile();
            if (file is null)
            {
                return;
            }

            _library.SetCover(game.Name, file, setFront: false);
            ScheduleSaveGameData();
            await ReloadDataViewsAsync(includeCharts: false);
        }

        private async void Btn_AddScreenshots_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGame is null)
            {
                return;
            }

            var files = PromptForImageFiles();
            if (files.Length == 0)
            {
                return;
            }

            _library.AddScreenshots(SelectedGame.Name, files);
            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: false);
            if (SelectedGame is not null)
            {
                OpenDrawer(SelectedGame);
            }
        }

        private async void ScreenshotDelete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGame is null
                || sender is not FrameworkElement element
                || element.DataContext is not ScreenshotViewModel screenshot)
            {
                return;
            }

            _library.RemoveScreenshot(SelectedGame.Name, screenshot.ImagePath);
            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: false);
            if (SelectedGame is not null)
            {
                OpenDrawer(SelectedGame);
            }
        }

        private void Btn_PreviousScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (!CanShowPreviousScreenshot)
            {
                return;
            }

            _screenshotIndex--;
            RaiseScreenshotNavigationProperties();
        }

        private void Btn_NextScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (!CanShowNextScreenshot)
            {
                return;
            }

            _screenshotIndex++;
            RaiseScreenshotNavigationProperties();
        }

        private async void Btn_DeleteCurrentScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGame is null || CurrentScreenshot is null)
            {
                return;
            }

            _library.RemoveScreenshot(SelectedGame.Name, CurrentScreenshot.ImagePath);
            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: false);
            _screenshotIndex = Math.Max(0, _screenshotIndex - 1);
            if (SelectedGame is not null)
            {
                OpenDrawer(SelectedGame);
            }
        }

        private async void CardSetScore_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCardViewModel(sender, out var game))
            {
                return;
            }

            var input = Microsoft.VisualBasic.Interaction.InputBox($"为游戏 \"{game.Name}\" 输入评分 (0-10)：", "设置评分", game.Score.ToString());
            if (!int.TryParse(input, out var score))
            {
                return;
            }

            score = Math.Clamp(score, 0, 10);
            _library.SetScore(game.Name, score);
            ScheduleSaveGameData();
            ApplyLibraryViewState();
            await ReloadDataViewsAsync(includeCharts: true);
        }

        private async void CardAddTime_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCardViewModel(sender, out var game))
            {
                return;
            }

            var hours = game.TotalSeconds / 3600;
            var minutes = (game.TotalSeconds % 3600) / 60;
            var input = Microsoft.VisualBasic.Interaction.InputBox(
                $"修改游戏 \"{game.Name}\" 的总游玩时长（格式：小时 分钟，如 1 30）：",
                "修改时长",
                $"{hours} {minutes:D2}");

            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            var parts = input.Trim().Split(new[] { ' ', '，', ',', '：', ':' }, StringSplitOptions.RemoveEmptyEntries);
            var totalSeconds = -1;
            if (parts.Length >= 2 && int.TryParse(parts[0], out var inputHours) && int.TryParse(parts[1], out var inputMinutes))
            {
                totalSeconds = inputHours * 3600 + inputMinutes * 60;
            }
            else if (parts.Length >= 1 && int.TryParse(parts[0], out var totalMinutes))
            {
                totalSeconds = totalMinutes * 60;
            }

            if (totalSeconds < 0)
            {
                return;
            }

            _library.SetTotalSeconds(game.Name, totalSeconds, DateTime.Now);
            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: true);
        }

        private async void CardSwitchExe_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCardViewModel(sender, out var game))
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "可执行文件|*.exe",
                Title = $"选择 {game.Name} 的启动程序"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            _library.SetLaunchExe(game.Name, dialog.FileName);
            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: false);
        }

        private void CardName_Click(object sender, RoutedEventArgs e)
        {
            if (TryGetCardViewModel(sender, out var game))
            {
                LaunchGame(game.Name);
            }
        }

        private async void CardDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCardViewModel(sender, out var game))
            {
                return;
            }

            var result = MessageBox.Show(
                $"确定要删除游戏 \"{game.Name}\" 吗？\n此操作无法撤销。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var deletedSelectedGame = ReferenceEquals(SelectedGame, game);
            _library.DeleteGame(game.Name);
            if (deletedSelectedGame)
            {
                CloseDrawer();
            }

            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: true);
        }

        private async void CardRename_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCardViewModel(sender, out var game))
            {
                return;
            }

            var input = Microsoft.VisualBasic.Interaction.InputBox(
                $"为游戏 \"{game.Name}\" 输入新名称：",
                "重命名游戏",
                game.Name);

            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            var newName = input.Trim();
            if (string.Equals(game.Name, newName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!_library.RenameGame(game.Name, newName))
            {
                MessageBox.Show($"游戏 \"{newName}\" 已存在，请使用其他名称。", "重命名失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _data.SaveGameData();
            await ReloadDataViewsAsync(includeCharts: true);
        }

        private void LaunchGame(string gameName)
        {
            if (!_library.TryGetLaunchExe(gameName, out var executablePath) || string.IsNullOrWhiteSpace(executablePath))
            {
                MessageBox.Show("该游戏尚未设置启动文件。", "错误");
                return;
            }

            if (!File.Exists(executablePath))
            {
                MessageBox.Show($"游戏启动文件不存在：{executablePath}\n请重新选择启动程序。", "错误");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动游戏失败：{ex.Message}", "错误");
            }
        }

        private static string? PromptForImageFile()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp"
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private static string[] PromptForImageFiles()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp",
                Multiselect = true
            };

            return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
        }

        private static bool TryGetCardViewModel(object sender, out GameViewModel game)
        {
            if (sender is FrameworkElement element && element.DataContext is GameViewModel viewModel)
            {
                game = viewModel;
                return true;
            }

            game = null!;
            return false;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                if (_trayIcon is not null)
                {
                    _trayIcon.Visible = true;
                }
            }
        }

        private void CleanupResources()
        {
            _monitor.Stop();
            _trayIcon?.Dispose();
            _trayIconImage?.Dispose();
            _searchDebounceTimer?.Stop();
            _saveDebounceTimer?.Stop();
            _imageCache.Dispose();
        }

        private static void HookNestedScrolling(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is ScrollViewer innerScrollViewer)
                {
                    innerScrollViewer.PreviewMouseWheel += (_, args) =>
                    {
                        if (args.Handled)
                        {
                            return;
                        }

                        var atTop = args.Delta > 0 && innerScrollViewer.VerticalOffset <= 0;
                        var atBottom = args.Delta < 0 && innerScrollViewer.VerticalOffset >= innerScrollViewer.ScrollableHeight;
                        if (!atTop && !atBottom)
                        {
                            return;
                        }

                        args.Handled = true;
                        var outerScrollViewer = FindOuterScrollViewer(innerScrollViewer);
                        outerScrollViewer?.ScrollToVerticalOffset(outerScrollViewer.VerticalOffset - args.Delta);
                    };
                }

                HookNestedScrolling(child);
            }
        }

        private static ScrollViewer? FindOuterScrollViewer(DependencyObject element)
        {
            var parent = VisualTreeHelper.GetParent(element);
            while (parent is not null)
            {
                if (parent is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }

                parent = VisualTreeHelper.GetParent(parent);
            }

            return null;
        }
    }
}
