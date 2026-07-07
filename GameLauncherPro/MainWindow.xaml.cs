using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
using MediaColor = System.Windows.Media.Color;

namespace GameLauncherPro
{
    public partial class MainWindow : Window
    {
        private readonly GameDataService _data = new();
        private readonly ChartService _chartService = new();
        private readonly ImageCacheService _imageCache;
        private readonly GameLibraryController _library;

        private readonly TimeSpan _chartUpdateIntervalOnBattery = TimeSpan.FromMinutes(2);

        private ProcessMonitorService _monitor = null!;
        private WinForms.NotifyIcon? _trayIcon;
        private DispatcherTimer? _searchDebounceTimer;
        private DispatcherTimer? _saveDebounceTimer;
        private DateTime _lastChartUpdateTime = DateTime.MinValue;

        private CartesianChart? _barChart;
        private PieChart? _pieChart;

        public MainWindow()
        {
            InitializeComponent();

            _data.LoadConfig();
            _data.LoadGameData();

            _imageCache = new ImageCacheService(_data.GetAppDataDir());
            _library = new GameLibraryController(_data, _imageCache);

            DataContext = this;
            InitializeTimers();
            InitializeCharts();
            InitializeProcessMonitor();
            InitializeTrayIcon();

            ApplyConfigToUI();
            ApplyLibraryViewState();
            _ = ReloadDataViewsAsync(includeCharts: true);

            Loaded += (_, _) => HookNestedScrolling(this);
            Closed += (_, _) => CleanupResources();
        }

        public System.ComponentModel.ICollectionView GamesView => _library.GamesView;

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
                        ? new SolidColorBrush(MediaColor.FromRgb(0x3F, 0xB9, 0x50))
                        : new SolidColorBrush(MediaColor.FromRgb(0x48, 0x4F, 0x58));
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
                var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executablePath))
                {
                    _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
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
            _library.ApplyViewState(Tb_Search.Text, GetSelectedSortOption());
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

            var labelColor = new SkiaSharp.SKColor(0xA8, 0x90, 0x78);
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
                    Fill = new SolidColorPaint(new SkiaSharp.SKColor(0xD4, 0xA5, 0x74)),
                    Stroke = new SolidColorPaint(new SkiaSharp.SKColor(0x8B, 0x5A, 0x2B)) { StrokeThickness = 1 }
                }
            };

            var pieColors = new[]
            {
                new SkiaSharp.SKColor(0xD4, 0xA5, 0x74),
                new SkiaSharp.SKColor(0x8B, 0x5A, 0x2B),
                new SkiaSharp.SKColor(0xE8, 0xC5, 0x8A),
                new SkiaSharp.SKColor(0xA0, 0x7A, 0x38),
                new SkiaSharp.SKColor(0xF0, 0xD5, 0x9C),
                new SkiaSharp.SKColor(0xC9, 0x8E, 0x4A),
                new SkiaSharp.SKColor(0x9B, 0x76, 0x2F),
                new SkiaSharp.SKColor(0xCC, 0xA0, 0x50),
                new SkiaSharp.SKColor(0xB8, 0x86, 0x3B),
                new SkiaSharp.SKColor(0xDA, 0xB0, 0x60)
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
                    Stroke = new SolidColorPaint(new SkiaSharp.SKColor(0x3A, 0x2E, 0x28)) { StrokeThickness = 1 }
                });
            }

            _pieChart.Series = pieSeries.ToArray();
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

            _library.DeleteGame(game.Name);
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
