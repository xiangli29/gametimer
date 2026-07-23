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
        private volatile bool _isStopping;

        private readonly Dictionary<string, DateTime> _runningGameStartTimes = new();
        private readonly Dictionary<string, string> _runningGameExePaths = new();
        private readonly Dictionary<string, string> _runningGameNames = new();
        // PID cache: full Process.GetProcesses() only every 30s
        private readonly Dictionary<string, int> _trackedPids = new(StringComparer.OrdinalIgnoreCase);
        private int _tickCounter;

        public HashSet<string> CachedExes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, string> RunningGameExePaths
        {
            get
            {
                lock (_data.DataLock)
                {
                    return _runningGameExePaths.ToDictionary(
                        item => _runningGameNames.TryGetValue(item.Key, out var name) ? name : item.Key,
                        item => item.Value,
                        StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public event Action<string, bool>? RunningStateUpdated;

        public ProcessMonitorService(GameDataService data, Action<bool> onTick, Action onScheduleSave)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _onTick = onTick ?? throw new ArgumentNullException(nameof(onTick));
            _onScheduleSave = onScheduleSave ?? throw new ArgumentNullException(nameof(onScheduleSave));
        }

        public void Start()
        {
            _isStopping = false;
            _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(CHECK_INTERVAL) };
            _monitorTimer.Tick += MonitorTick;
            _monitorTimer.Start();
            _powerCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _powerCheckTimer.Tick += (s, e) => AdjustTimerForPower();
            _powerCheckTimer.Start();
        }

        public void Stop()
        {
            _isStopping = true;
            try { _monitorTimer?.Stop(); } catch { }
            try { _powerCheckTimer?.Stop(); } catch { }
            if (FinalizeStoppedGames(Array.Empty<string>(), DateTime.Now))
            {
                _data.SaveGameData();
            }
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

        public void TrackLaunchedGame(string gameName, string executablePath, int processId)
        {
            if (_isStopping || processId <= 0)
            {
                return;
            }

            var identity = _data.ResolveGameForExecutable(gameName, executablePath);
            AddOrUpdateRunningGame(identity, executablePath);
            lock (_data.DataLock)
            {
                _trackedPids[identity.Id] = processId;
            }
            RunningStateUpdated?.Invoke(identity.Name + " | " + FormatTime(0) + "\n", true);
        }

        private void MonitorTick(object? sender, EventArgs e)
        {
            if (_isStopping || _isMonitoringBusy) return;
            _isMonitoringBusy = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (_isStopping || string.IsNullOrEmpty(_data.GameRootDir)) return;
                    // Full scan control: first tick + every 30s
                    _tickCounter++;
                    bool doFullScan = _tickCounter == 1 || _tickCounter >= 6;
                    if (doFullScan && _tickCounter >= 6) _tickCounter = 0;

                    int currentPid = Process.GetCurrentProcess().Id;
                    var runningGameIds = new HashSet<string>(StringComparer.Ordinal);

                    if (doFullScan)
                    {
                        // Full scan: enumerate all processes (only every 30s)
                        foreach (var process in Process.GetProcesses())
                        {
                            try
                            {
                                if (!TryMatchProcess(process, currentPid, out var fallbackName, out var exePath))
                                    continue;
                                var identity = _data.ResolveGameForExecutable(fallbackName, exePath);
                                runningGameIds.Add(identity.Id);
                                AddOrUpdateRunningGame(identity, exePath);
                                _trackedPids[identity.Id] = process.Id;
                            }
                            catch { }
                        }
                    }
                    // Directly launched games are known by PID before their window appears, so
                    // check them on every tick, including a full-scan tick.
                    foreach (var kv in _trackedPids.ToList())
                    {
                        try
                        {
                            var p = Process.GetProcessById(kv.Value);
                            if (p.HasExited) continue;
                            string? exePath = null;
                            try { exePath = p.MainModule?.FileName; } catch { }

                            if (_data.TryGetGameById(kv.Key, out var identity))
                            {
                                if (string.IsNullOrEmpty(exePath))
                                {
                                    lock (_data.DataLock)
                                    {
                                        _runningGameExePaths.TryGetValue(identity.Id, out exePath);
                                    }
                                }

                                if (!string.IsNullOrEmpty(exePath))
                                {
                                    runningGameIds.Add(identity.Id);
                                    AddOrUpdateRunningGame(identity, exePath);
                                }
                            }
                        }
                        catch
                        {
                            // PID no longer exists, handled by stopped-games logic below.
                        }
                    }

                    // Build display text (outside the lock!)
                    var runningDisplay = new List<string>(runningGameIds.Count);
                    foreach (var gameId in runningGameIds)
                    {
                        int elapsed;
                        string gameName;
                        lock (_data.DataLock)
                        {
                            elapsed = _runningGameStartTimes.TryGetValue(gameId, out var start)
                                ? (int)(DateTime.Now - start).TotalSeconds
                                : 0;
                            gameName = _runningGameNames.TryGetValue(gameId, out var name) ? name : "Unknown Game";
                        }
                        runningDisplay.Add(gameName + " | " + FormatTime(elapsed));
                    }
                    RunningStateUpdated?.Invoke(
                        runningDisplay.Count > 0 ? string.Join("\n", runningDisplay) + "\n" : "",
                        runningDisplay.Count > 0);
                    var hasStoppedGames = FinalizeStoppedGames(runningGameIds, DateTime.Now);
                    if (hasStoppedGames && _isStopping)
                    {
                        _data.SaveGameData();
                    }
                    else if (hasStoppedGames)
                    {
                        _onScheduleSave();
                    }
                    _onTick(hasStoppedGames);
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

        private void AddOrUpdateRunningGame(GameDataService.GameIdentity identity, string exePath)
        {
            lock (_data.DataLock)
            {
                _runningGameExePaths[identity.Id] = exePath;
                _runningGameNames[identity.Id] = identity.Name;
                if (!_runningGameStartTimes.ContainsKey(identity.Id))
                    _runningGameStartTimes[identity.Id] = DateTime.Now;
            }
        }

        private bool FinalizeStoppedGames(IEnumerable<string> activeGameIds, DateTime endedAt)
        {
            var activeIds = new HashSet<string>(activeGameIds, StringComparer.Ordinal);
            var completed = new List<(string GameId, DateTime StartedAt)>();
            lock (_data.DataLock)
            {
                foreach (var gameId in _runningGameStartTimes.Keys.ToList())
                {
                    if (activeIds.Contains(gameId))
                    {
                        continue;
                    }

                    completed.Add((gameId, _runningGameStartTimes[gameId]));
                    _runningGameStartTimes.Remove(gameId);
                    _runningGameExePaths.Remove(gameId);
                    _runningGameNames.Remove(gameId);
                    _trackedPids.Remove(gameId);
                }
            }

            foreach (var session in completed)
            {
                _data.CompletePlaySession(session.GameId, session.StartedAt, endedAt);
            }

            return completed.Count > 0;
        }

private static string FormatTime(int seconds)
        {
            int h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
            return h.ToString("00") + "\u5c0f\u65f6" + m.ToString("00") + "\u5206\u949f" + s.ToString("00") + "\u79d2";
        }
    }
}
