using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Rectangle = System.Windows.Shapes.Rectangle;
using Image = System.Windows.Controls.Image;
using Path = System.Windows.Shapes.Path;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using IOPath = System.IO.Path;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WPF;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.Painting;
// using System.Windows.Forms; // removed to avoid ambiguous references with WinForms alias
using WpfControls = System.Windows.Controls;
using WpfMedia = System.Windows.Media;
using WpfShapes = System.Windows.Shapes;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using System.Windows.Shapes;
using GameLauncherPro.Services;
using System.Drawing;
using System.Windows.Input;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;
using GameLauncherPro.ViewModels;

namespace GameLauncherPro
{
    public partial class MainWindow : Window
    {
        // ====================== 全局配置 ======================
        private const string CONFIG_FILE = "config.json";
        private const string DATA_FILE = "game_play_time.json";
        private const int CHECK_INTERVAL = 5;
        private const int GAMES_PER_ROW = 5;
        private readonly GameDataService _data = new();
        private ProcessMonitorService _monitor = null!;
        private System.Windows.Forms.NotifyIcon? _trayIcon;


        private bool gameDataDirty = true;
        // 图片加载并发/取消支持
        private System.Threading.CancellationTokenSource? imageLoadCts;
        private readonly System.Threading.SemaphoreSlim imageLoadSemaphore = new System.Threading.SemaphoreSlim(4);

        // 防抖定时器
        private DispatcherTimer? searchDebounceTimer;
        private DispatcherTimer? saveDebounceTimer;

        // 图表金色配色（和你UI统一）
        private readonly Brush[] GoldColors = new Brush[]
        {
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(212,175,55)),
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(255,215,0)),
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(184,134,11)),
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(139,115,85)),
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(205,133,63))
        };

        // GameData moved to GameLauncherPro.GameData

        private void ScheduleSaveGameData()
        {
            try
            {
                saveDebounceTimer?.Stop();
                saveDebounceTimer?.Start();
            }
            catch { Task.Run(() => _data.SaveGameData()); }
        }

        private void Tb_Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 防抖：延迟 300ms 再刷新，避免输入时频繁重建 UI
            try
            {
                searchDebounceTimer?.Stop();
                searchDebounceTimer?.Start();
            }
            catch { RefreshUI(); }
        }

        private void Cb_Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshUI();
        }

        // Apply configuration to UI controls
        private void ApplyConfigToUI()
        {
            try
            {
                Rb_Manual.IsChecked = _data.AutoRefreshMode == AutoRefreshModeEnum.Manual;
                Rb_AutoOnAC.IsChecked = _data.AutoRefreshMode == AutoRefreshModeEnum.AutoOnAC;
                Rb_Always.IsChecked = _data.AutoRefreshMode == AutoRefreshModeEnum.Always;
                Cb_StrongPower.IsChecked = _data.StrongPowerSaving;
            }
            catch { }
            // 兼容旧字段
            _data.AutoRefreshCharts = _data.AutoRefreshMode == AutoRefreshModeEnum.Always || (_data.AutoRefreshMode == AutoRefreshModeEnum.AutoOnAC && ProcessMonitorService.IsOnACPower());
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

        public MainWindow()
        {
            InitializeComponent();
            _data.LoadConfig();
            _data.LoadGameData();

            _monitor = new ProcessMonitorService(_data, () => Dispatcher.BeginInvoke(new Action(() => { gameDataDirty = true; RefreshUI_PowerAware(); }), System.Windows.Threading.DispatcherPriority.Background), ScheduleSaveGameData);
            _monitor.RunningStateUpdated += (displayText, hasRunning) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Tb_Running.Text = displayText;
                    RunningIndicator.Fill = hasRunning
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50))
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x48, 0x4F, 0x58));
                }), System.Windows.Threading.DispatcherPriority.Background);
            };

            // 初始化数据绑定集合
            Games = new ObservableCollection<GameViewModel>();
            this.DataContext = this;
            // 初始化防抖定时器：搜索与保存
            searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            searchDebounceTimer.Tick += (s, e) => { searchDebounceTimer?.Stop(); RefreshUI(); };

            saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            saveDebounceTimer.Tick += (s, e) => { saveDebounceTimer?.Stop(); _data.SaveGameData(); };

            _monitor.Start();
            if (!string.IsNullOrEmpty(_data.GameRootDir)) _monitor.ScanAllGameExes();

            // 初始化系统托盘
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "GameLauncherPro",
                Visible = false
            };
            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch { }
            _trayIcon.DoubleClick += (s, e) => ShowFromTray();
            var trayMenu = new System.Windows.Forms.ContextMenuStrip();
            trayMenu.Items.Add("显示", null, (s, e) => ShowFromTray());
            trayMenu.Items.Add("退出", null, (s, e) => { _trayIcon.Visible = false; _trayIcon.Dispose(); System.Windows.Application.Current.Shutdown(); });
            _trayIcon.ContextMenuStrip = trayMenu;

            InitializeCharts();
            ApplyConfigToUI();
            PopulateGameCollectionFromData();
            RefreshUI();

            // 窗口加载完成后初始化图表 + 嵌套滚动
            Loaded += (s, e) =>
            {
                RefreshCharts();
                HookNestedScrolling(this);
            };
            EnsureThumbnailFolder();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                _monitor.Stop();
                _trayIcon?.Dispose();
                imageLoadCts?.Cancel();
                imageLoadSemaphore?.Dispose();
                searchDebounceTimer?.Stop();
                saveDebounceTimer?.Stop();
            }
            catch { }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_trayIcon != null) _trayIcon.Visible = false;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                if (_trayIcon != null) _trayIcon.Visible = true;
            }
        }

        // 供 XAML 绑定的集合
        public ObservableCollection<GameViewModel> Games { get; set; } = new();

        private GameViewModel? FindViewModelByName(string name)
        {
            return Games.FirstOrDefault(g => g.Name == name);
        }

        private void PopulateGameCollectionFromData()
        {
            // Cancel any ongoing image loads for previous population
            try
            {
                imageLoadCts?.Cancel();
            }
            catch { }
            imageLoadCts = new System.Threading.CancellationTokenSource();

            // Build list of vms off UI thread then apply to ObservableCollection on UI thread
            Task.Run(async () =>
            {
                var list = new List<GameViewModel>();
                List<KeyValuePair<string, GameData>> snapshot;
                lock (_data.DataLock)
                {
                    snapshot = _data.GameData.ToList();
                }

                foreach (var kv in snapshot)
                {
                    var vm = new GameViewModel();
                    vm.UpdateFromGameData(kv.Key, kv.Value);
                    list.Add(vm);
                    // Start async image load but do not block building list
                    if (!string.IsNullOrEmpty(kv.Value.cover_path) && File.Exists(kv.Value.cover_path))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await imageLoadSemaphore.WaitAsync(imageLoadCts.Token);
                                if (imageLoadCts.IsCancellationRequested) return;
                                var bi = new BitmapImage();
                                bi.BeginInit();
                                bi.UriSource = new Uri(kv.Value.cover_path, UriKind.Absolute);
                                bi.DecodePixelWidth = 240;
                                bi.CacheOption = BitmapCacheOption.OnLoad;
                                bi.EndInit();
                                bi.Freeze();
                                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => { vm.FrontImage = bi; }), DispatcherPriority.Background);
                            }
                            catch { }
                            finally { try { imageLoadSemaphore.Release(); } catch { } }
                        }, imageLoadCts.Token);
                    }

                    if (!string.IsNullOrEmpty(kv.Value.cover_back_path) && File.Exists(kv.Value.cover_back_path))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await imageLoadSemaphore.WaitAsync(imageLoadCts.Token);
                                if (imageLoadCts.IsCancellationRequested) return;
                                var bi = new BitmapImage();
                                bi.BeginInit();
                                bi.UriSource = new Uri(kv.Value.cover_back_path, UriKind.Absolute);
                                bi.DecodePixelWidth = 240;
                                bi.CacheOption = BitmapCacheOption.OnLoad;
                                bi.EndInit();
                                bi.Freeze();
                                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => { vm.BackImage = bi; }), DispatcherPriority.Background);
                            }
                            catch { }
                            finally { try { imageLoadSemaphore.Release(); } catch { } }
                        }, imageLoadCts.Token);
                    }
                }

                // Apply to ObservableCollection on UI thread
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Games.Clear();
                    foreach (var vm in list) Games.Add(vm);
                }));
            });
        }

        private void LoadImageForViewModel(string path, GameViewModel vm, bool isFront)
        {
            Task.Run(() =>
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(path, UriKind.Absolute);
                    bi.DecodePixelWidth = 240;
                    bi.DecodePixelHeight = 320;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (isFront) vm.FrontImage = bi; else vm.BackImage = bi;
                    }), DispatcherPriority.Background);
                }
                catch { }
            });
        }


        private void EnsureThumbnailFolder()
        {
            try
            {
                var dir = GetThumbnailDirectory();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var appDir = _data.GetAppDataDir();
                if (!Directory.Exists(appDir)) Directory.CreateDirectory(appDir);
            }
            catch { }
        }


        private string GetThumbnailDirectory()
        {
            return System.IO.Path.Combine(_data.GetAppDataDir(), "thumbnails");
        }
        private string GetThumbnailPath(string originalPath)
        {
            try
            {
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(originalPath.ToLowerInvariant()));
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                var file = System.IO.Path.GetFileNameWithoutExtension(originalPath);
                var thumbName = file + "_" + sb.ToString() + ".jpg";
                return System.IO.Path.Combine(GetThumbnailDirectory(), thumbName);
            }
            catch { return null; }
        }

        private string GetAppDataDir()
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLauncherPro");
            return dir;
        }

        private void LoadImageAsync(string originalPath, Image target, System.Windows.Shapes.Rectangle placeholder, int desiredWidth, int desiredHeight, Func<bool> shouldShowWhenLoaded)
        {
            if (string.IsNullOrEmpty(originalPath) || !File.Exists(originalPath)) return;
            var thumb = GetThumbnailPath(originalPath);
            // If thumbnail exists, load it (off UI thread)
            Task.Run(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(thumb) && File.Exists(thumb))
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(thumb, UriKind.Absolute);
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        bi.Freeze();
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            target.Source = bi;
                            // hide placeholder when any image loaded
                            if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;
                            // show image only if it's the active side
                            try { target.Visibility = shouldShowWhenLoaded?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed; } catch { target.Visibility = Visibility.Collapsed; }
                        });
                        return;
                    }

                    // create thumbnail from original
                    var bi2 = new BitmapImage();
                    bi2.BeginInit();
                    bi2.UriSource = new Uri(originalPath, UriKind.Absolute);
                    if (desiredWidth > 0) bi2.DecodePixelWidth = desiredWidth;
                    if (desiredHeight > 0) bi2.DecodePixelHeight = desiredHeight;
                    bi2.CacheOption = BitmapCacheOption.OnLoad;
                    bi2.EndInit();
                    bi2.Freeze();

                    // Save thumbnail
                    try
                    {
                        if (!string.IsNullOrEmpty(thumb))
                        {
                            var encoder = new JpegBitmapEncoder();
                            encoder.QualityLevel = 85;
                            encoder.Frames.Add(BitmapFrame.Create(bi2));
                            using var fs = File.Open(thumb, FileMode.Create);
                            encoder.Save(fs);
                        }
                    }
                    catch { }

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        target.Source = bi2;
                        if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;
                        try { target.Visibility = shouldShowWhenLoaded?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed; } catch { target.Visibility = Visibility.Collapsed; }
                    });
                }
                catch { }
            });
        }

        // LiveCharts 控件引用（XAML 中的控件）
        private CartesianChart? barChart;
        private PieChart? pieChart;
        // 控制是否自动刷新图表（可用于节电）

        private void InitializeCharts()
        {
            // 从 XAML 元素获得引用（使用 FindName 避免在 XAML 类型解析阶段的符号问题）
            barChart = this.FindName("Lc_BarChart") as CartesianChart;
            pieChart = this.FindName("Lc_PieChart") as PieChart;
            // 初始清空
            if (barChart != null) { barChart.Series = new ISeries[] { }; barChart.XAxes = new Axis[] { new Axis() }; barChart.YAxes = new Axis[] { new Axis() }; }
            if (pieChart != null) pieChart.Series = new ISeries[] { };
        }

        private void AppendLog(string path, string text)
        {
            try
            {
                File.AppendAllText(path, text + Environment.NewLine);
            }
            catch { }
        }


        // 扫描目录下所有 exe，返回完整路径列表


        private DateTime lastChartUpdateTime = DateTime.MinValue;
        private readonly TimeSpan chartUpdateIntervalOnBattery = TimeSpan.FromMinutes(2);

        private void RefreshUI_PowerAware()
        {
            if (gameDataDirty) { RenderGameLibrary(); gameDataDirty = false; }
            RenderRecord();
            UpdateStatusBar();

            // 根据用户设置和电源状态决定图表刷新策略
            bool shouldRefresh = false;
            switch (_data.AutoRefreshMode)
            {
                case AutoRefreshModeEnum.Always:
                    shouldRefresh = true;
                    break;
                case AutoRefreshModeEnum.AutoOnAC:
                    shouldRefresh = ProcessMonitorService.IsOnACPower();
                    break;
                case AutoRefreshModeEnum.Manual:
                default:
                    shouldRefresh = false;
                    break;
            }

            // 强省电模式下即使允许刷新也做额外限制
            if (_data.StrongPowerSaving && !ProcessMonitorService.IsOnACPower())
            {
                shouldRefresh = false;
            }

            if (!shouldRefresh)
            {

                List<KeyValuePair<string, GameData>> sortedData;
                lock (_data.DataLock) { sortedData = _data.GameData.OrderByDescending(x => x.Value.total_seconds).ToList(); }

                DrawRank(sortedData);
                return;
            }

            // 否则在允许的条件下按频率刷新
            if (ProcessMonitorService.IsOnACPower())
            {
                RefreshCharts();
                lastChartUpdateTime = DateTime.Now;
            }
            else
            {
                if ((DateTime.Now - lastChartUpdateTime) > chartUpdateIntervalOnBattery)
                {
                    RefreshCharts();
                    lastChartUpdateTime = DateTime.Now;
                }
                else
                {

                    List<KeyValuePair<string, GameData>> sortedData;
                lock (_data.DataLock) { sortedData = _data.GameData.OrderByDescending(x => x.Value.total_seconds).ToList(); }

                    DrawRank(sortedData);
                }
            }
        }

        // ====================== 游戏库卡片 ======================
        private void RenderGameLibrary()
        {
            // 使用数据过滤/排序生成视图模型集合，ItemsControl 通过绑定显示 Games

            List<KeyValuePair<string, GameData>> list;
            lock (_data.DataLock) { list = _data.GameData.ToList(); }

            var query = (this.FindName("Tb_Search") as System.Windows.Controls.TextBox)?.Text ?? "";
            if (!string.IsNullOrWhiteSpace(query))
                list = list.Where(kv => kv.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var filterBox = this.FindName("Cb_Filter") as System.Windows.Controls.ComboBox;
            if (filterBox != null && filterBox.SelectedItem is System.Windows.Controls.ComboBoxItem ci)
            {
                var content = ci.Content.ToString();
                if (content == "按游玩时间（从大到小）")
                    list = list.OrderByDescending(x => x.Value.total_seconds).ToList();
                else if (content == "按最近打开（从近到远）")
                    list = list.OrderByDescending(x => DateTime.TryParse(x.Value.last_play, out var d) ? d : DateTime.MinValue).ToList();
                else if (content == "按评分（从高到低）")
                    list = list.OrderByDescending(x => x.Value.score).ToList();
            }

            // 重建 Games 集合以匹配过滤后的顺序
            Games.Clear();
            foreach (var kv in list)
            {
                var vm = new GameViewModel();
                vm.UpdateFromGameData(kv.Key, kv.Value);
                if (!string.IsNullOrEmpty(kv.Value.cover_path) && File.Exists(kv.Value.cover_path))
                    LoadImageForViewModel(kv.Value.cover_path, vm, true);
                if (!string.IsNullOrEmpty(kv.Value.cover_back_path) && File.Exists(kv.Value.cover_back_path))
                    LoadImageForViewModel(kv.Value.cover_back_path, vm, false);
                Games.Add(vm);
            }
        }

        // 统一的卡片操作处理器（基于 GameViewModel）
        private void CardFlip_Click(GameViewModel vm)
        {
            if (vm == null) return;
            var name = vm.Name;
            lock (_data.DataLock)
            {
                if (!_data.GameData.ContainsKey(name)) _data.GameData[name] = new GameData();
                _data.GameData[name].current_side = _data.GameData[name].current_side == "back" ? "front" : "back";
            }
            ScheduleSaveGameData();
            // 重新填充视图模型并重绘
            PopulateGameCollectionFromData();
            if (gameDataDirty) { RenderGameLibrary(); gameDataDirty = false; }
        }

        private void CardSetFront_Click(GameViewModel vm)
        {
            if (vm == null) return;
            var dialog = new OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog() == true)
            {
                lock (_data.DataLock)
                {
                    if (!_data.GameData.ContainsKey(vm.Name)) _data.GameData[vm.Name] = new GameData();
                    _data.GameData[vm.Name].cover_path = dialog.FileName;
                }
                ScheduleSaveGameData();
                PopulateGameCollectionFromData();
                if (gameDataDirty) { RenderGameLibrary(); gameDataDirty = false; }
            }
        }

        private void CardSetBack_Click(GameViewModel vm)
        {
            if (vm == null) return;
            var dialog = new OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog() == true)
            {
                lock (_data.DataLock)
                {
                    if (!_data.GameData.ContainsKey(vm.Name)) _data.GameData[vm.Name] = new GameData();
                    _data.GameData[vm.Name].cover_back_path = dialog.FileName;
                }
                ScheduleSaveGameData();
                PopulateGameCollectionFromData();
                if (gameDataDirty) { RenderGameLibrary(); gameDataDirty = false; }
            }
        }

        private void CardSetScore_Click(GameViewModel vm)
        {
            if (vm == null) return;
            var input = Microsoft.VisualBasic.Interaction.InputBox($"为游戏 '{vm.Name}' 输入评分 (0-10)：", "设置评分", "");
            if (int.TryParse(input, out var s))
            {
                s = Math.Max(0, Math.Min(10, s));
                lock (_data.DataLock)
                {
                    if (!_data.GameData.ContainsKey(vm.Name)) _data.GameData[vm.Name] = new GameData();
                    _data.GameData[vm.Name].score = s;
                }
                ScheduleSaveGameData();
                PopulateGameCollectionFromData();
                if (gameDataDirty) { RenderGameLibrary(); gameDataDirty = false; }
            }
        }

        private void CardName_Click(GameViewModel vm)
        {
            if (vm == null) return;
            LaunchGame(vm.Name);
        }

        private void ChooseAndSetCover(string gameName, bool setFront)
        {
            var dialog = new OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog() == true)
            {
                lock (_data.DataLock)
                {
                    if (!_data.GameData.ContainsKey(gameName)) _data.GameData[gameName] = new GameData();
                    if (setFront)
                        _data.GameData[gameName].cover_path = dialog.FileName;
                    else
                        _data.GameData[gameName].cover_back_path = dialog.FileName;
                }
                // 更新 view model
                var vm = FindViewModelByName(gameName);
                if (vm != null)
                {
                    if (setFront) LoadImageForViewModel(dialog.FileName, vm, true); else LoadImageForViewModel(dialog.FileName, vm, false);
                }
                ScheduleSaveGameData();
                PopulateGameCollectionFromData();
            }
        }

        private void PromptSetScore(string gameName)
        {
            var input = Microsoft.VisualBasic.Interaction.InputBox($"为游戏 '{gameName}' 输入评分 (0-10)：", "设置评分", "");
            if (int.TryParse(input, out var s))
            {
                s = Math.Max(0, Math.Min(10, s));
                lock (_data.DataLock)
                {
                    if (!_data.GameData.ContainsKey(gameName)) _data.GameData[gameName] = new GameData();
                    _data.GameData[gameName].score = s;
                }
                var vm = FindViewModelByName(gameName);
                if (vm != null) vm.Score = s;
                ScheduleSaveGameData();
                PopulateGameCollectionFromData();
            }
        }

        // DataTemplate 按钮事件处理：通过 sender.DataContext 获取 GameViewModel
        private void CardFlip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameViewModel vm)
            {
                lock (_data.DataLock)
                {
                    if (!_data.GameData.ContainsKey(vm.Name)) _data.GameData[vm.Name] = new GameData();
                    _data.GameData[vm.Name].current_side = _data.GameData[vm.Name].current_side == "back" ? "front" : "back";
                    vm.CurrentSide = _data.GameData[vm.Name].current_side;
                }
                ScheduleSaveGameData();
            }
        }

        private void CardSetFront_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameViewModel vm)
            {
                ChooseAndSetCover(vm.Name, true);
            }
        }

        private void CardSetBack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameViewModel vm)
            {
                ChooseAndSetCover(vm.Name, false);
            }
        }

        private void CardSetScore_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameViewModel vm)
            {
                PromptSetScore(vm.Name);
            }
        }

        private void CardAddTime_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameViewModel vm)
            {
                var input = Microsoft.VisualBasic.Interaction.InputBox(
                    "为游戏 '" + vm.Name + "' 手动补录时长（格式：小时 分钟，如 1 30）：", "补录时长", "");
                if (!string.IsNullOrWhiteSpace(input))
                {
                    var parts = input.Trim().Split(new[] { ' ', '\uff0c', ',', '\uff1a', ':' }, StringSplitOptions.RemoveEmptyEntries);
                    int totalSec = 0;
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
                        totalSec = h * 3600 + m * 60;
                    else if (int.TryParse(parts[0], out var mins))
                        totalSec = mins * 60;
                    if (totalSec > 0)
                    {
                        lock (_data.DataLock)
                        {
                            if (!_data.GameData.ContainsKey(vm.Name)) _data.GameData[vm.Name] = new GameData();
                            _data.GameData[vm.Name].total_seconds += totalSec;
                            _data.GameData[vm.Name].last_play = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        }
                        _data.SaveGameData();
                        gameDataDirty = true;
                        RefreshUI();
                    }
                }
            }
        }

        private void CardName_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameViewModel vm)
            {
                LaunchGame(vm.Name);
            }
        }

        // ====================== 封面/启动游戏 ======================
        private void SetCover(string gameName)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp"
            };
            if (dialog.ShowDialog() == true)
            {
                lock (_data.DataLock)
                {
                    if (!_data.GameData.ContainsKey(gameName)) _data.GameData[gameName] = new GameData();
                    _data.GameData[gameName].cover_path = dialog.FileName;
                }
                _data.SaveGameData();
                if (gameDataDirty) { RenderGameLibrary(); gameDataDirty = false; }
            }
        }

        private void LaunchGame(string gameName)
        {
            string exe;
            lock (_data.DataLock) { exe = _data.GameData.ContainsKey(gameName) ? _data.GameData[gameName].launch_exe : string.Empty; }
            if (File.Exists(exe))
            {
                try
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"启动游戏失败：{ex.Message}", "错误");
                }
            }
            else
            {
                MessageBox.Show($"游戏启动文件不存在：{exe}\n请重新选择游戏文件夹", "错误");
            }
        }


        // 嵌套滚动：内层 ScrollViewer 到底/到顶后继续滚动外层
        private static void HookNestedScrolling(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer innerSv)
                {
                    innerSv.PreviewMouseWheel += (s, args) =>
                    {
                        if (args.Handled) return;
                        var sv = (ScrollViewer)s;
                        bool atTop = args.Delta > 0 && sv.VerticalOffset <= 0;
                        bool atBottom = args.Delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight;
                        if (atTop || atBottom)
                        {
                            args.Handled = true;
                            // 找到最外层 ScrollViewer 并手动滚动
                            var outer = FindOuterScrollViewer(sv);
                            outer?.ScrollToVerticalOffset(outer.VerticalOffset - args.Delta);
                        }
                    };
                }
                HookNestedScrolling(child);
            }
        }

        private static ScrollViewer? FindOuterScrollViewer(DependencyObject element)
        {
            var parent = VisualTreeHelper.GetParent(element);
            while (parent != null)
            {
                if (parent is ScrollViewer sv) return sv;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }
        private string FormatTime(int seconds)
        {
            int h = seconds / 3600;
            int m = (seconds % 3600) / 60;
            int s = seconds % 60;
            return $"{h:00}小时{m:00}分钟{s:00}秒";
        }

        // 原生绘图已由 LiveCharts 替代，旧的 DrawBarChart/DrawPieChart 方法已移除。

        // ====================== ✅ 排行榜 ======================
        private void DrawRank(List<KeyValuePair<string, GameData>> data)
        {
            Tb_Rank.Text = "";
            int totalSec = data.Sum(x => x.Value.total_seconds);
            Tb_Rank.Text += $"总时长：{FormatTime(totalSec)}\n========================\n";

            for (int i = 0; i < data.Count; i++)
            {
                var item = data[i];
                Tb_Rank.Text += $"{i + 1}. {item.Key}\n   {FormatTime(item.Value.total_seconds)}\n\n";
            }
        }

        // ====================== 统一刷新图表 ======================
        private void RefreshCharts()
        {

            List<KeyValuePair<string, GameData>> sortedData;
            lock (_data.DataLock) { sortedData = _data.GameData.OrderByDescending(x => x.Value.total_seconds).ToList(); }

            RefreshLiveCharts(sortedData);
            DrawRank(sortedData);
        }

        
        private void RefreshLiveCharts(List<KeyValuePair<string, GameData>> sortedData)
        {
            if (barChart == null || pieChart == null) return;

            var labels = sortedData.Select(x => x.Key).ToArray();
            var values = sortedData.Select(x => (double)x.Value.total_seconds).ToArray();

            // 柱状图
            var labelColor = new SkiaSharp.SKColor(0xA8, 0x90, 0x78);
            barChart.XAxes = new Axis[] { new Axis { Labels = labels, LabelsRotation = 45, LabelsPaint = new SolidColorPaint(labelColor) } };
            barChart.YAxes = new Axis[] { new Axis { Labeler = v => FormatTime((int)v), LabelsPaint = new SolidColorPaint(labelColor) } };

            var columnSeries = new LiveChartsCore.SkiaSharpView.ColumnSeries<double>
            {
                Values = values,
                Fill = new SolidColorPaint(new SkiaSharp.SKColor(0xD4, 0xA5, 0x74)),
                Stroke = new SolidColorPaint(new SkiaSharp.SKColor(0x8B, 0x5A, 0x2B)) { StrokeThickness = 1 }
            };
            barChart.Series = new ISeries[] { columnSeries };

            // 饼图：为每个游戏分配区分色
            var pieColors = new SkiaSharp.SKColor[]
            {
                new(0xD4, 0xA5, 0x74), new(0x8B, 0x5A, 0x2B), new(0xE8, 0xC5, 0x8A),
                new(0xA0, 0x7A, 0x38), new(0xF0, 0xD5, 0x9C), new(0xC9, 0x8E, 0x4A),
                new(0x9B, 0x76, 0x2F), new(0xCC, 0xA0, 0x50), new(0xB8, 0x86, 0x3B),
                new(0xDA, 0xB0, 0x60)
            };
            var pieSeries = new List<ISeries>();
            for (int i = 0; i < sortedData.Count; i++)
            {
                var item = sortedData[i];
                var color = pieColors[i % pieColors.Length];
                pieSeries.Add(new LiveChartsCore.SkiaSharpView.PieSeries<double>
                {
                    Values = new double[] { item.Value.total_seconds },
                    Name = item.Key,
                    Fill = new SolidColorPaint(color),
                    Stroke = new SolidColorPaint(new SkiaSharp.SKColor(0x3A, 0x2E, 0x28)) { StrokeThickness = 1 }
                });
            }
            pieChart.Series = pieSeries.ToArray();

            try { barChart.Background = (System.Windows.Media.Brush)FindResource("ChartBgBrush"); } catch { }
        }

        // ====================== 历史记录面板 ======================
        private void RenderRecord()
        {
            Tb_Record.Text = "";
            Tb_Record.Text += $"统计时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
            Tb_Record.Text += $"监控目录：{_data.GameRootDir}\n\n";


            List<KeyValuePair<string, GameData>> recordSnapshot;
            lock (_data.DataLock) { recordSnapshot = _data.GameData.ToList(); }
            foreach (var kv in recordSnapshot)
            {
                var name = kv.Key;
                var data = kv.Value;
                Tb_Record.Text += $"游戏：{name}\n";
                Tb_Record.Text += $"总时长：{FormatTime(data.total_seconds)}\n";
                Tb_Record.Text += $"最后游玩：{data.last_play}\n";
                Tb_Record.Text += $"启动路径：{data.launch_exe}\n";
                Tb_Record.Text += new string('-', 50) + "\n\n";
            }
        }

        private void RefreshUI()
        {
            RenderGameLibrary();
            RenderRecord();
            RefreshCharts();
            UpdateStatusBar();
        }

        // ====================== 按钮事件 ======================
        private void Btn_ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog();
            dialog.Description = "请选择游戏总文件夹";
            dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            dialog.ShowNewFolderButton = true;
            var result = dialog.ShowDialog();
            if (result == WinForms.DialogResult.OK || result == WinForms.DialogResult.Yes)
            {
                _data.GameRootDir = dialog.SelectedPath;
                _data.SaveConfig();
                _monitor.CachedExes.Clear();
                // 异步预扫描新目录以填充缓存，加快后续监控匹配
                Task.Run(() => _monitor.ScanAllGameExes());
                // 不再自动扫描目录
                MessageBox.Show("已设置监控目录。请通过运行游戏并点击“将当前游戏加入库”来添加游戏。", "提示");
            }
        }

        private void Btn_AddGame_Click(object sender, RoutedEventArgs e)
        {
            if (_monitor.RunningGameExePaths.Count == 0)
            {
                MessageBox.Show("请先运行游戏，程序将自动识别运行中的游戏。", "提示");
                return;
            }

            // 取第一个运行的游戏加入库
            var first = _monitor.RunningGameExePaths.First();
            string gameName = first.Key;
            string exePath = first.Value;

            lock (_data.DataLock)
            {
                if (!_data.GameData.ContainsKey(gameName)) _data.GameData[gameName] = new GameData();
                _data.GameData[gameName].launch_exe = exePath;
                if (!_data.GameData[gameName].exe_paths.Contains(exePath)) _data.GameData[gameName].exe_paths.Add(exePath);
            }

            _data.SaveGameData();
            RefreshUI();
            MessageBox.Show($"已将游戏 {gameName} 添加到库中", "成功");
        }

        private void Btn_Refresh_Click(object sender, RoutedEventArgs e)
        {
            _data.LoadGameData();
            // 手动刷新数据并界面
            RefreshUI();
            MessageBox.Show("数据已刷新！(注意：现在不自动扫描目录，请使用“将当前游戏加入库”添加游戏)", "提示");
        }


        // ====================== 底部状态栏 ======================
        private void UpdateStatusBar()
        {
            try
            {
                Tb_StatusDir.Text = string.IsNullOrEmpty(_data.GameRootDir)
                    ? "\u672A\u8BBE\u7F6E\u76D1\u63A7\u76EE\u5F55"
                    : $"监控目录: {_data.GameRootDir}";
                int gameCount;
                int totalSec;
                lock (_data.DataLock)
                {
                    gameCount = _data.GameData.Count;
                    totalSec = _data.GameData.Values.Sum(x => x.total_seconds);
                }
                Tb_StatusNum.Text = $"共 {gameCount} 款游戏";
                Tb_StatusTotal.Text = $"总时长: {FormatTime(totalSec)}";
                Tb_GameCount.Text = gameCount > 0 ? $"({gameCount} 款)" : "";
            }
            catch { }
        }
    }
}
