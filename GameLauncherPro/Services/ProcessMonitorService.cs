using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameLauncherPro.Services
{
    public class ProcessMonitorService
    {
        private readonly GameDataService _data;
        private readonly Action _onRefreshUI;
        private readonly Action _onScheduleSave;

        private const int CHECK_INTERVAL = 5;
        private DispatcherTimer? _monitorTimer;
        private DispatcherTimer? _powerCheckTimer;
        private volatile bool _isMonitoringBusy;

        private readonly Dictionary<string, DateTime> _runningGameStartTimes = new();
        private readonly Dictionary<string, string> _runningGameExePaths = new();

        public HashSet<string> CachedExes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, string> RunningGameExePaths => _runningGameExePaths;

        public event Action<string, bool>? RunningStateUpdated;

        public ProcessMonitorService(GameDataService data, Action onRefreshUI, Action onScheduleSave)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _onRefreshUI = onRefreshUI ?? throw new ArgumentNullException(nameof(onRefreshUI));
            _onScheduleSave = onScheduleSave ?? throw new ArgumentNullException(nameof(onScheduleSave));
        }

        public void Start()
        {
            _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(CHECK_INTERVAL) };
            _monitorTimer.Tick += MonitorTick;
            _monitorTimer.Start();
            _powerCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _powerCheckTimer.Tick += (s, e) => AdjustTimerForPower();
            _powerCheckTimer.Start();
        }

        public void Stop()
        {
            try { _monitorTimer?.Stop(); } catch { }
            try { _powerCheckTimer?.Stop(); } catch { }
        }

        public static bool IsOnACPower()
        {
            try { return SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online; }
            catch { return true; }
        }

        private void AdjustTimerForPower()
        {
            if (_monitorTimer == null) return;
            bool onAC = IsOnACPower();
            _monitorTimer.Interval = onAC ? TimeSpan.FromSeconds(CHECK_INTERVAL) : TimeSpan.FromSeconds(Math.Max(30, CHECK_INTERVAL));
            if (_data.AutoRefreshMode == AutoRefreshModeEnum.AutoOnAC) _data.AutoRefreshCharts = onAC && !_data.StrongPowerSaving;
            else if (_data.AutoRefreshMode == AutoRefreshModeEnum.Manual) _data.AutoRefreshCharts = false;
            else if (_data.AutoRefreshMode == AutoRefreshModeEnum.Always) _data.AutoRefreshCharts = !_data.StrongPowerSaving;
        }

        public void ScanAllGameExes()
        {
            if (string.IsNullOrEmpty(_data.GameRootDir) || !Directory.Exists(_data.GameRootDir)) return;
            try { CachedExes.Clear(); foreach (var f in Directory.EnumerateFiles(_data.GameRootDir, "*.exe", SearchOption.AllDirectories)) CachedExes.Add(Path.GetFileNameWithoutExtension(f)); } catch { }
        }

        private void MonitorTick(object? sender, EventArgs e)
        {
            if (_isMonitoringBusy) return;
            _isMonitoringBusy = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(_data.GameRootDir)) return;
                    int currentPid = Process.GetCurrentProcess().Id;
                    var runningExes = new List<string>();
                    var runningDisplay = new List<string>();
                    foreach (var process in Process.GetProcesses())
                    {
                        try
                        {
                            if (process.Id == currentPid) continue;
                            if (!CachedExes.Contains(process.ProcessName)) continue;
                            if (string.IsNullOrEmpty(process.MainWindowTitle)) continue;
                            string? exePath = null;
                            try { exePath = process.MainModule?.FileName; } catch { }
                            if (string.IsNullOrEmpty(exePath)) continue;
                            if (!exePath.StartsWith(_data.GameRootDir, StringComparison.OrdinalIgnoreCase)) continue;
                            string gameName = Path.GetFileName(Path.GetDirectoryName(exePath) ?? "未知游戏");
                            lock (_data.DataLock)
                            {
                                _runningGameExePaths[gameName] = exePath;
                                if (!_runningGameStartTimes.ContainsKey(gameName)) _runningGameStartTimes[gameName] = DateTime.Now;
                                runningExes.Add(gameName);
                                int elapsed = (int)(DateTime.Now - _runningGameStartTimes[gameName]).TotalSeconds;
                                runningDisplay.Add(gameName + " | 已游玩：" + FormatTime(elapsed));
                            }
                        }
                        catch { }
                    }
                    RunningStateUpdated?.Invoke(string.Join("\n", runningDisplay) + (runningDisplay.Count > 0 ? "\n" : ""), runningDisplay.Count > 0);
                    var stoppedGames = new List<string>();
                    lock (_data.DataLock)
                    {
                        foreach (var game in _runningGameStartTimes.Keys.ToList())
                        {
                            if (!runningExes.Contains(game))
                            {
                                if (!_data.GameData.ContainsKey(game)) _data.GameData[game] = new GameData();
                                int duration = (int)(DateTime.Now - _runningGameStartTimes[game]).TotalSeconds;
                                _data.GameData[game].total_seconds += duration;
                                _data.GameData[game].last_play = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                if (_runningGameExePaths.TryGetValue(game, out var se))
                                {
                                    if (!_data.GameData[game].exe_paths.Contains(se)) _data.GameData[game].exe_paths.Add(se);
                                    if (string.IsNullOrEmpty(_data.GameData[game].launch_exe)) _data.GameData[game].launch_exe = se;
                                }
                                stoppedGames.Add(game);
                            }
                        }
                        foreach (var g in stoppedGames) { _runningGameStartTimes.Remove(g); _runningGameExePaths.Remove(g); }
                    }
                    if (stoppedGames.Count > 0) _onScheduleSave();
                    _onRefreshUI();
                }
                finally { _isMonitoringBusy = false; }
            });
        }

        private static string FormatTime(int seconds)
        {
            int h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
            return h.ToString("00") + "小时" + m.ToString("00") + "分钟" + s.ToString("00") + "秒" + m.ToString("00") + "åˆ†é'Ÿ" + s.ToString("00") + "ç§'";
        }
    }
}
