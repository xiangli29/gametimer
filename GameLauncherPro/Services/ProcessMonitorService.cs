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
        private readonly Action<bool> _onTick;
        private readonly Action _onScheduleSave;

        private const int CHECK_INTERVAL = 5;
        private DispatcherTimer? _monitorTimer;
        private DispatcherTimer? _powerCheckTimer;
        private volatile bool _isMonitoringBusy;

        private readonly Dictionary<string, DateTime> _runningGameStartTimes = new();
        private readonly Dictionary<string, string> _runningGameExePaths = new();
        // PID cache: full Process.GetProcesses() only every 30s
        private readonly Dictionary<string, int> _trackedPids = new(StringComparer.OrdinalIgnoreCase);
        private int _tickCounter;

        public HashSet<string> CachedExes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, string> RunningGameExePaths => _runningGameExePaths;

        public event Action<string, bool>? RunningStateUpdated;

        public ProcessMonitorService(GameDataService data, Action<bool> onTick, Action onScheduleSave)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _onTick = onTick ?? throw new ArgumentNullException(nameof(onTick));
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
                    // Full scan control: first tick + every 30s
                    _tickCounter++;
                    bool doFullScan = _tickCounter == 1 || _tickCounter >= 6;
                    if (doFullScan && _tickCounter >= 6) _tickCounter = 0;

                    int currentPid = Process.GetCurrentProcess().Id;
                    var runningExes = new List<string>(_trackedPids.Count + 4);

                    if (doFullScan)
                    {
                        // Full scan: enumerate all processes (only every 30s)
                        foreach (var process in Process.GetProcesses())
                        {
                            try
                            {
                                if (!TryMatchProcess(process, currentPid, out var gameName, out var exePath))
                                    continue;
                                runningExes.Add(gameName);
                                AddOrUpdateRunningGame(gameName, exePath);
                                _trackedPids[gameName] = process.Id;
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        // Lightweight: only check cached PIDs (no system-wide enumeration)
                        foreach (var kv in _trackedPids.ToList())
                        {
                            try
                            {
                                var p = Process.GetProcessById(kv.Value);
                                if (p.HasExited) continue;
                                if (string.IsNullOrEmpty(p.MainWindowTitle)) continue;
                                if (!CachedExes.Contains(p.ProcessName)) continue;
                                string? exePath = null;
                                try { exePath = p.MainModule?.FileName; } catch { }
                                if (string.IsNullOrEmpty(exePath)) continue;
                                if (!exePath.StartsWith(_data.GameRootDir, StringComparison.OrdinalIgnoreCase)) continue;

                                runningExes.Add(kv.Key);
                                lock (_data.DataLock)
                                {
                                    _runningGameExePaths[kv.Key] = exePath;
                                }
                            }
                            catch
                            {
                                // PID no longer exists, handled by stopped-games logic below
                            }
                        }
                    }

                    // Build display text (outside the lock!)
                    var runningDisplay = new List<string>(runningExes.Count);
                    foreach (var gameName in runningExes)
                    {
                        int elapsed;
                        lock (_data.DataLock)
                        {
                            elapsed = _runningGameStartTimes.TryGetValue(gameName, out var start)
                                ? (int)(DateTime.Now - start).TotalSeconds
                                : 0;
                        }
                        runningDisplay.Add(gameName + " | " + FormatTime(elapsed));
                    }
                    RunningStateUpdated?.Invoke(
                        runningDisplay.Count > 0 ? string.Join("\n", runningDisplay) + "\n" : "",
                        runningDisplay.Count > 0);
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
                    _onTick(stoppedGames.Count > 0);
                }
                finally { _isMonitoringBusy = false; }
            });
        }

        
        private bool TryMatchProcess(Process process, int currentPid, out string gameName, out string exePath)
        {
            gameName = null!;
            exePath = null!;

            if (process.Id == currentPid) return false;
            if (!CachedExes.Contains(process.ProcessName)) return false;
            if (string.IsNullOrEmpty(process.MainWindowTitle)) return false;

            try { exePath = process.MainModule?.FileName ?? ""; } catch { return false; }
            if (string.IsNullOrEmpty(exePath)) return false;
            if (!exePath.StartsWith(_data.GameRootDir, StringComparison.OrdinalIgnoreCase)) return false;

            gameName = Path.GetFileName(Path.GetDirectoryName(exePath) ?? "\u672a\u77e5\u6e38\u620f");
            return true;
        }

        private void AddOrUpdateRunningGame(string gameName, string exePath)
        {
            lock (_data.DataLock)
            {
                _runningGameExePaths[gameName] = exePath;
                if (!_runningGameStartTimes.ContainsKey(gameName))
                    _runningGameStartTimes[gameName] = DateTime.Now;
            }
        }

private static string FormatTime(int seconds)
        {
            int h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
            return h.ToString("00") + "\u5c0f\u65f6" + m.ToString("00") + "\u5206\u949f" + s.ToString("00") + "\u79d2";
        }
    }
}
