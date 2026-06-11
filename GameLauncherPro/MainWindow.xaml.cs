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
        private string GAME_ROOT_DIR = "";
        // 运行中游戏名 -> 会话开始时间，用于计算实时时长
        private readonly Dictionary<string, DateTime> runningGameStartTimes = new();
        // 运行中游戏名 -> exe 路径，跨 tick 保存，供“加入库”使用
        private readonly Dictionary<string, string> runningGameExePaths = new();
        private Dictionary<string, GameData> gameData = new();
        // 同步对象，保护对共享集合的并发访问
        private readonly object dataLock = new object();

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
            catch { Task.Run(() => SaveGameData()); }
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
                Rb_Manual.IsChecked = AutoRefreshMode == AutoRefreshModeEnum.Manual;
                Rb_AutoOnAC.IsChecked = AutoRefreshMode == AutoRefreshModeEnum.AutoOnAC;
                Rb_Always.IsChecked = AutoRefreshMode == AutoRefreshModeEnum.Always;
                Cb_StrongPower.IsChecked = strongPowerSaving;
            }
            catch { }
            // 兼容旧字段
            AutoRefreshCharts = AutoRefreshMode == AutoRefreshModeEnum.Always || (AutoRefreshMode == AutoRefreshModeEnum.AutoOnAC && IsOnACPower());
        }

        private void Rb_AutoManual_Checked(object sender, RoutedEventArgs e)
        {
            AutoRefreshMode = AutoRefreshModeEnum.Manual;
            ApplyConfigToUI();
            SaveConfig();
        }

        private void Rb_AutoOnAC_Checked(object sender, RoutedEventArgs e)
        {
            AutoRefreshMode = AutoRefreshModeEnum.AutoOnAC;
            ApplyConfigToUI();
            SaveConfig();
        }

        private void Rb_AutoAlways_Checked(object sender, RoutedEventArgs e)
        {
            AutoRefreshMode = AutoRefreshModeEnum.Always;
            ApplyConfigToUI();
            SaveConfig();
        }

        private void Cb_StrongPower_Checked(object sender, RoutedEventArgs e)
        {
            strongPowerSaving = Cb_StrongPower.IsChecked == true;
            SaveConfig();
        }

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();
            LoadGameData();
            // 初始化数据绑定集合
            Games = new ObservableCollection<GameViewModel>();
            this.DataContext = this;
            // 初始化防抖定时器：搜索与保存
            searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            searchDebounceTimer.Tick += (s, e) => { searchDebounceTimer?.Stop(); RefreshUI(); };

            saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            saveDebounceTimer.Tick += (s, e) => { saveDebounceTimer?.Stop(); SaveGameData(); };

            StartMonitorTimer();
            InitializeCharts();
            ApplyConfigToUI();
            PopulateGameCollectionFromData();
            RefreshUI();
            EnsureThumbnailFolder();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                imageLoadCts?.Cancel();
                imageLoadSemaphore?.Dispose();
                searchDebounceTimer?.Stop();
                saveDebounceTimer?.Stop();
            }
            catch { }
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
                lock (dataLock)
                {
                    snapshot = gameData.ToList();
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
                var appDir = GetAppDataDir();
                if (!Directory.Exists(appDir)) Directory.CreateDirectory(appDir);
            }
            catch { }
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
        private enum AutoRefreshModeEnum { Manual = 0, AutoOnAC = 1, Always = 2 }
        private AutoRefreshModeEnum AutoRefreshMode = AutoRefreshModeEnum.Manual;
        private bool AutoRefreshCharts = false; // 兼容旧逻辑
        private bool strongPowerSaving = false;

        private void InitializeCharts()
        {
            // 从 XAML 元素获得引用（使用 FindName 避免在 XAML 类型解析阶段的符号问题）
            barChart = this.FindName("Lc_BarChart") as CartesianChart;
            pieChart = this.FindName("Lc_PieChart") as PieChart;
            // 初始清空
            if (barChart != null) barChart.Series = new ISeries[] { };
            if (pieChart != null) pieChart.Series = new ISeries[] { };
        }

        private bool IsOnACPower()
        {
            try
            {
                var status = SystemInformation.PowerStatus;
                return status.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            }
            catch
            {
                return true; // 默认认为插电，避免降低实时性
            }
        }

        // ====================== 配置/数据读写 ======================
        private void LoadConfig()
        {
            var cfgPath = GetConfigFilePath();
            if (File.Exists(cfgPath))
            {
                try
                {
                    var config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cfgPath));
                    GAME_ROOT_DIR = config.TryGetValue("game_dir", out var dir) ? dir : "";
                    if (config.TryGetValue("auto_refresh_mode", out var modeStr) && int.TryParse(modeStr, out var m))
                        AutoRefreshMode = Enum.IsDefined(typeof(AutoRefreshModeEnum), m) ? (AutoRefreshModeEnum)m : AutoRefreshMode;
                    AutoRefreshCharts = config.TryGetValue("auto_refresh_charts", out var ar) && bool.TryParse(ar, out var val) ? val : AutoRefreshCharts;
                    strongPowerSaving = config.TryGetValue("strong_power_saving", out var sps) && bool.TryParse(sps, out var s) ? s : strongPowerSaving;
                    // 不自动扫描目录，改为手动通过“添加到游戏库”按钮添加游戏条目
                }
                catch { }
            }
        }

        private void SaveConfig()
        {
            var cfg = new Dictionary<string, string>
            {
                ["game_dir"] = GAME_ROOT_DIR ?? "",
                ["auto_refresh_charts"] = AutoRefreshCharts.ToString(),
                ["auto_refresh_mode"] = ((int)AutoRefreshMode).ToString(),
                ["strong_power_saving"] = strongPowerSaving.ToString()
            };
            try
            {
                var cfgPath = GetConfigFilePath();
                var dir = IOPath.GetDirectoryName(cfgPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置失败：{ex.Message}", "错误");
            }
        }

        private void LoadGameData()
        {
            var dataPath = GetDataFilePath();
            if (File.Exists(dataPath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, GameData>>(File.ReadAllText(dataPath))
                               ?? new Dictionary<string, GameData>();
                    lock (dataLock)
                    {
                        gameData = loaded;
                    }
                }
                catch
                {
                    lock (dataLock) { gameData = new Dictionary<string, GameData>(); }
                }
            }
            else
            {
                lock (dataLock) { gameData = new Dictionary<string, GameData>(); }
            }
        }

        private void SaveGameData()
        {
            var dataPath = GetDataFilePath();
            var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "save_log.txt");
            try
            {
                var dir = IOPath.GetDirectoryName(dataPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // serialize to string first
                Dictionary<string, GameData> snapshot;
                lock (dataLock) { snapshot = new Dictionary<string, GameData>(gameData); }
                var content = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                // write to temp file then move to ensure atomic replace
                var tmp = dataPath + ".tmp";
                File.WriteAllText(tmp, content);
                // replace existing file
                try
                {
                    if (File.Exists(dataPath))
                    {
                        File.Delete(dataPath);
                    }
                    File.Move(tmp, dataPath);
                }
                catch (Exception mvEx)
                {
                    // fallback: attempt overwrite copy
                    try
                    {
                        File.Copy(tmp, dataPath, true);
                        File.Delete(tmp);
                    }
                    catch (Exception copyEx)
                    {
                        AppendLog(logPath, $"保存数据移动失败: {mvEx.Message}; 复制失败: {copyEx.Message}");
                        MessageBox.Show($"保存数据失败：{copyEx.Message}", "错误");
                        return;
                    }
                }

                AppendLog(logPath, $"SaveGameData success: wrote {dataPath} ({DateTime.Now:O})");
            }
            catch (Exception ex)
            {
                AppendLog(logPath, $"SaveGameData exception: {ex.Message}");
                MessageBox.Show($"保存数据失败：{ex.Message}", "错误");
            }
        }

        private void AppendLog(string path, string text)
        {
            try
            {
                File.AppendAllText(path, text + Environment.NewLine);
            }
            catch { }
        }

        private string GetDataFilePath()
        {
            return System.IO.Path.Combine(GetAppDataDir(), DATA_FILE);
        }

        private string GetConfigFilePath()
        {
            return System.IO.Path.Combine(GetAppDataDir(), CONFIG_FILE);
        }

        private string GetThumbnailDirectory()
        {
            return System.IO.Path.Combine(GetAppDataDir(), "thumbnails");
        }

        // ====================== 扫描游戏目录 ======================
        private void ScanAndAddGames()
        {
            if (!Directory.Exists(GAME_ROOT_DIR)) return;

            foreach (var dir in Directory.GetDirectories(GAME_ROOT_DIR))
            {
                string gameName = System.IO.Path.GetFileName(dir);
                var exes = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories);
                if (exes.Length == 0) continue;

                lock (dataLock)
                {
                    if (!gameData.ContainsKey(gameName))
                        gameData[gameName] = new GameData();

                    gameData[gameName].launch_exe = exes[0];
                    if (!gameData[gameName].exe_paths.Contains(exes[0]))
                        gameData[gameName].exe_paths.Add(exes[0]);
                }
            }
            ScheduleSaveGameData();
        }

        // 扫描目录下所有 exe，返回完整路径列表
        private HashSet<string> cachedExes = new(StringComparer.OrdinalIgnoreCase);
        // 防止 MonitorTick 重入
        private bool isMonitoringBusy = false;
        private bool gameDataDirty = true;

        private static readonly SolidColorBrush RunningIndicatorOn = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50));
        private static readonly SolidColorBrush RunningIndicatorOff = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x48, 0x4F, 0x58));
                // 防抖定时器：搜索框、合并写入 (已在类顶部声明)

        private List<string> ScanAllGameExes()
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(GAME_ROOT_DIR) || !Directory.Exists(GAME_ROOT_DIR))
                return result;

            try
            {
                foreach (var file in Directory.EnumerateFiles(GAME_ROOT_DIR, "*.exe", SearchOption.AllDirectories))
                {
                    var p = System.IO.Path.GetFullPath(file);
                    result.Add(p);
                    cachedExes.Add(System.IO.Path.GetFileNameWithoutExtension(p));
                }
            }
            catch { }

            return result;
        }

        // ====================== 进程监控 ======================
        private DispatcherTimer? monitorTimer;

        private void StartMonitorTimer()
        {
            monitorTimer = new DispatcherTimer();
            monitorTimer.Interval = TimeSpan.FromSeconds(CHECK_INTERVAL);
            monitorTimer.Tick += MonitorTick;
            monitorTimer.Start();
            // 根据电源状态定期检查并调整间隔
            var powerCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            powerCheckTimer.Tick += (s, e) => AdjustTimerForPower();
            powerCheckTimer.Start();
        }

        private void AdjustTimerForPower()
        {
            if (monitorTimer == null) return;
            bool onAC = IsOnACPower();
            if (onAC)
            {
                monitorTimer.Interval = TimeSpan.FromSeconds(CHECK_INTERVAL);
            }
            else
            {
                // 电池模式下降低频率以节省电量
                monitorTimer.Interval = TimeSpan.FromSeconds(Math.Max(30, CHECK_INTERVAL));
            }

            // 根据 AutoRefreshMode 自动启用/禁用图表自动刷新
            if (AutoRefreshMode == AutoRefreshModeEnum.AutoOnAC)
            {
                AutoRefreshCharts = onAC && !strongPowerSaving;
            }
            else if (AutoRefreshMode == AutoRefreshModeEnum.Manual)
            {
                AutoRefreshCharts = false;
            }
            else if (AutoRefreshMode == AutoRefreshModeEnum.Always)
            {
                AutoRefreshCharts = !strongPowerSaving; // 若强省电则关闭
            }
        }

        private void MonitorTick(object? sender, EventArgs e)
        {
            if (isMonitoringBusy) return;
            isMonitoringBusy = true;
            // Run heavy processing off UI thread to avoid blocking and causing UI flicker
            Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(GAME_ROOT_DIR)) return;
                    int currentPid = Process.GetCurrentProcess().Id;

                    var runningExes = new List<string>();
                    var runningDisplay = new List<string>();

                    foreach (var process in Process.GetProcesses())
                    {
                        try
                        {
                            if (process.Id == currentPid) continue;
                            if (!IsOnACPower())
                            {
                                string procName = process.ProcessName;
                                if (!cachedExes.Contains(procName)) continue;

                            }

                            if (string.IsNullOrEmpty(process.MainWindowTitle)) continue;

                            string exePath = null;
                            try { exePath = process.MainModule?.FileName; } catch { exePath = null; }
                            if (string.IsNullOrEmpty(exePath)) continue;
                            if (!exePath.StartsWith(GAME_ROOT_DIR, StringComparison.OrdinalIgnoreCase)) continue;

                            string gameName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(exePath) ?? "未知游戏");

                            lock (dataLock)
                            {
                                runningGameExePaths[gameName] = exePath;
                                if (!runningGameStartTimes.ContainsKey(gameName)) runningGameStartTimes[gameName] = DateTime.Now;
                                runningExes.Add(gameName);
                                int elapsed = (int)(DateTime.Now - runningGameStartTimes[gameName]).TotalSeconds;
                                runningDisplay.Add($"{gameName} | 已游玩：{FormatTime(elapsed)}");
                            }
                        }
                        catch { }
                    }

                    // Update running text on UI thread once
                    try
                    {
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Tb_Running.Text = string.Join("\n", runningDisplay) + (runningDisplay.Count > 0 ? "\n" : "");
                                RunningIndicator.Fill = (runningDisplay.Count > 0) ? RunningIndicatorOn : RunningIndicatorOff;
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                    catch { }

                    // Handle stopped games and update persistent data under lock
                    List<string> stoppedGames = new List<string>();
                    lock (dataLock)
                    {
                        foreach (var game in runningGameStartTimes.Keys.ToList())
                        {
                            if (!runningExes.Contains(game))
                            {
                                if (!gameData.ContainsKey(game)) gameData[game] = new GameData();
                                int duration = (int)(DateTime.Now - runningGameStartTimes[game]).TotalSeconds;
                                gameData[game].total_seconds += duration;
                                gameData[game].last_play = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                if (runningGameExePaths.TryGetValue(game, out var stoppedExe))
                                {
                                    if (!gameData[game].exe_paths.Contains(stoppedExe)) gameData[game].exe_paths.Add(stoppedExe);
                                    if (string.IsNullOrEmpty(gameData[game].launch_exe)) gameData[game].launch_exe = stoppedExe;
                                }
                                stoppedGames.Add(game);
                            }
                        }

                        foreach (var g in stoppedGames)
                        {
                            runningGameStartTimes.Remove(g);
                            runningGameExePaths.Remove(g);
                        }
                    }

                    if (stoppedGames.Count > 0)
                    {
                        gameDataDirty = true;
                        try
                        {
                            saveDebounceTimer?.Stop();
                            saveDebounceTimer?.Start();
                        }
                        catch { Task.Run(() => SaveGameData()); }
                    }
                    // Refresh UI in a power-aware manner on UI thread
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => RefreshUI_PowerAware()), System.Windows.Threading.DispatcherPriority.Background);
                }
                finally
                {
                    isMonitoringBusy = false;
                }
            });
        }

        private DateTime lastChartUpdateTime = DateTime.MinValue;
        private readonly TimeSpan chartUpdateIntervalOnBattery = TimeSpan.FromMinutes(2);

        private void RefreshUI_PowerAware()
        {
            if (gameDataDirty) { RenderGameLibrary(); gameDataDirty = false; }
            RenderRecord();
            UpdateStatusBar();

            // 根据用户设置和电源状态决定图表刷新策略
            bool shouldRefresh = false;
            switch (AutoRefreshMode)
            {
                case AutoRefreshModeEnum.Always:
                    shouldRefresh = true;
                    break;
                case AutoRefreshModeEnum.AutoOnAC:
                    shouldRefresh = IsOnACPower();
                    break;
                case AutoRefreshModeEnum.Manual:
                default:
                    shouldRefresh = false;
                    break;
            }

            // 强省电模式下即使允许刷新也做额外限制
            if (strongPowerSaving && !IsOnACPower())
            {
                shouldRefresh = false;
            }

            if (!shouldRefresh)
            {
                var sortedData = gameData.OrderByDescending(x => x.Value.total_seconds).ToList();
                DrawRank(sortedData);
                return;
            }

            // 否则在允许的条件下按频率刷新
            if (IsOnACPower())
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
                    var sortedData = gameData.OrderByDescending(x => x.Value.total_seconds).ToList();
                    DrawRank(sortedData);
                }
            }
        }

        // ====================== 游戏库卡片 ======================
        private void RenderGameLibrary()
        {
            // 使用数据过滤/排序生成视图模型集合，ItemsControl 通过绑定显示 Games
            var list = gameData.ToList();
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
            lock (dataLock)
            {
                if (!gameData.ContainsKey(name)) gameData[name] = new GameData();
                gameData[name].current_side = gameData[name].current_side == "back" ? "front" : "back";
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
                lock (dataLock)
                {
                    if (!gameData.ContainsKey(vm.Name)) gameData[vm.Name] = new GameData();
                    gameData[vm.Name].cover_path = dialog.FileName;
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
                lock (dataLock)
                {
                    if (!gameData.ContainsKey(vm.Name)) gameData[vm.Name] = new GameData();
                    gameData[vm.Name].cover_back_path = dialog.FileName;
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
                lock (dataLock)
                {
                    if (!gameData.ContainsKey(vm.Name)) gameData[vm.Name] = new GameData();
                    gameData[vm.Name].score = s;
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
                lock (dataLock)
                {
                    if (!gameData.ContainsKey(gameName)) gameData[gameName] = new GameData();
                    if (setFront)
                        gameData[gameName].cover_path = dialog.FileName;
                    else
                        gameData[gameName].cover_back_path = dialog.FileName;
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
                lock (dataLock)
                {
                    if (!gameData.ContainsKey(gameName)) gameData[gameName] = new GameData();
                    gameData[gameName].score = s;
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
                if (!gameData.ContainsKey(vm.Name)) gameData[vm.Name] = new GameData();
                gameData[vm.Name].current_side = gameData[vm.Name].current_side == "back" ? "front" : "back";
                vm.CurrentSide = gameData[vm.Name].current_side;
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
                lock (dataLock)
                {
                    if (!gameData.ContainsKey(gameName)) gameData[gameName] = new GameData();
                    gameData[gameName].cover_path = dialog.FileName;
                }
                SaveGameData();
                if (gameDataDirty) { RenderGameLibrary(); gameDataDirty = false; }
            }
        }

        private void LaunchGame(string gameName)
        {
            string exe;
            lock (dataLock) { exe = gameData.ContainsKey(gameName) ? gameData[gameName].launch_exe : string.Empty; }
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
            var sortedData = gameData.OrderByDescending(x => x.Value.total_seconds).ToList();
            RefreshLiveCharts(sortedData);
            DrawRank(sortedData);
        }

        
        private void RefreshLiveCharts(List<KeyValuePair<string, GameData>> sortedData)
        {
            if (barChart == null || pieChart == null) return;

            var labels = sortedData.Select(x => x.Key).ToArray();
            var values = sortedData.Select(x => (double)x.Value.total_seconds).ToArray();

            // 柱状图
            var columnSeries = new LiveChartsCore.SkiaSharpView.ColumnSeries<double>
            {
                Values = values,
                Fill = new SolidColorPaint(new SkiaSharp.SKColor(0xD4, 0xA5, 0x74)),
                Stroke = new SolidColorPaint(new SkiaSharp.SKColor(0x8B, 0x5A, 0x2B)) { StrokeThickness = 1 }
            };
            barChart.Series = new ISeries[] { columnSeries };

            var labelColor = new SkiaSharp.SKColor(0xA8, 0x90, 0x78);
            barChart.XAxes = new Axis[] { new Axis { Labels = labels, LabelsRotation = 45, LabelsPaint = new SolidColorPaint(labelColor) } };

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
            Tb_Record.Text += $"监控目录：{GAME_ROOT_DIR}\n\n";

            foreach (var (name, data) in gameData)
            {
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
                GAME_ROOT_DIR = dialog.SelectedPath;
                SaveConfig();
                cachedExes.Clear();
                // 异步预扫描新目录以填充缓存，加快后续监控匹配
                Task.Run(() => ScanAllGameExes());
                // 不再自动扫描目录
                MessageBox.Show("已设置监控目录。请通过运行游戏并点击“将当前游戏加入库”来添加游戏。", "提示");
            }
        }

        private void Btn_AddGame_Click(object sender, RoutedEventArgs e)
        {
            if (runningGameExePaths.Count == 0)
            {
                MessageBox.Show("请先运行游戏，程序将自动识别运行中的游戏。", "提示");
                return;
            }

            // 取第一个运行的游戏加入库
            var first = runningGameExePaths.First();
            string gameName = first.Key;
            string exePath = first.Value;

            lock (dataLock)
            {
                if (!gameData.ContainsKey(gameName)) gameData[gameName] = new GameData();
                gameData[gameName].launch_exe = exePath;
                if (!gameData[gameName].exe_paths.Contains(exePath)) gameData[gameName].exe_paths.Add(exePath);
            }

            SaveGameData();
            RefreshUI();
            MessageBox.Show($"已将游戏 {gameName} 添加到库中", "成功");
        }

        private void Btn_Refresh_Click(object sender, RoutedEventArgs e)
        {
            LoadGameData();
            // 手动刷新数据并界面
            RefreshUI();
            MessageBox.Show("数据已刷新！(注意：现在不自动扫描目录，请使用“将当前游戏加入库”添加游戏)", "提示");
        }


        // ====================== 底部状态栏 ======================
        private void UpdateStatusBar()
        {
            try
            {
                Tb_StatusDir.Text = string.IsNullOrEmpty(GAME_ROOT_DIR)
                    ? "\u672A\u8BBE\u7F6E\u76D1\u63A7\u76EE\u5F55"
                    : $"监控目录: {GAME_ROOT_DIR}";
                int gameCount;
                int totalSec;
                lock (dataLock)
                {
                    gameCount = gameData.Count;
                    totalSec = gameData.Values.Sum(x => x.total_seconds);
                }
                Tb_StatusNum.Text = $"共 {gameCount} 款游戏";
                Tb_StatusTotal.Text = $"总时长: {FormatTime(totalSec)}";
                Tb_GameCount.Text = gameCount > 0 ? $"({gameCount} 款)" : "";
            }
            catch { }
        }
    }
}
