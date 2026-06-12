using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace GameLauncherPro.Services
{
    public enum AutoRefreshModeEnum { Manual = 0, AutoOnAC = 1, Always = 2 }

    public class GameDataService
    {
        private const string CONFIG_FILE = "config.json";
        private const string DATA_FILE = "game_play_time.json";

        public object DataLock { get; } = new object();
        public string GameRootDir { get; set; } = "";
        public AutoRefreshModeEnum AutoRefreshMode { get; set; } = AutoRefreshModeEnum.Manual;
        public bool AutoRefreshCharts { get; set; } = false;
        public bool StrongPowerSaving { get; set; } = false;
        public Dictionary<string, GameData> GameData { get; private set; } = new();

        public Dictionary<string, GameData> GetSnapshot()
        { lock (DataLock) { return new Dictionary<string, GameData>(GameData); } }

        public void LoadConfig()
        {
            var cfgPath = GetConfigFilePath();
            if (File.Exists(cfgPath))
            {
                try
                {
                    var config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cfgPath));
                    GameRootDir = config.TryGetValue("game_dir", out var dir) ? dir : "";
                    if (config.TryGetValue("auto_refresh_mode", out var modeStr) && int.TryParse(modeStr, out var m))
                        AutoRefreshMode = Enum.IsDefined(typeof(AutoRefreshModeEnum), m) ? (AutoRefreshModeEnum)m : AutoRefreshMode;
                    AutoRefreshCharts = config.TryGetValue("auto_refresh_charts", out var ar) && bool.TryParse(ar, out var val) ? val : AutoRefreshCharts;
                    StrongPowerSaving = config.TryGetValue("strong_power_saving", out var sps) && bool.TryParse(sps, out var s) ? s : StrongPowerSaving;
                }
                catch { }
            }
        }

        public void SaveConfig()
        {
            var cfg = new Dictionary<string, string> { ["game_dir"] = GameRootDir ?? "", ["auto_refresh_charts"] = AutoRefreshCharts.ToString(), ["auto_refresh_mode"] = ((int)AutoRefreshMode).ToString(), ["strong_power_saving"] = StrongPowerSaving.ToString() };
            try { var p = GetConfigFilePath(); var d = Path.GetDirectoryName(p); if (!Directory.Exists(d)) Directory.CreateDirectory(d); File.WriteAllText(p, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { System.Windows.MessageBox.Show("Save config failed: " + ex.Message, "Error"); }
        }

        public void LoadGameData()
        {
            var dataPath = GetDataFilePath();
            if (File.Exists(dataPath))
            {
                try { var loaded = JsonSerializer.Deserialize<Dictionary<string, GameData>>(File.ReadAllText(dataPath)) ?? new(); lock (DataLock) { GameData = loaded; } }
                catch { lock (DataLock) { GameData = new(); } }
            }
            else { lock (DataLock) { GameData = new(); } }
        }

        public void SaveGameData()
        {
            var dataPath = GetDataFilePath();
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "save_log.txt");
            try
            {
                var dir = Path.GetDirectoryName(dataPath); if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Dictionary<string, GameData> snapshot; lock (DataLock) { snapshot = new Dictionary<string, GameData>(GameData); }
                var content = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                var tmp = dataPath + ".tmp"; File.WriteAllText(tmp, content);
                try { if (File.Exists(dataPath)) File.Delete(dataPath); File.Move(tmp, dataPath); }
                catch (Exception mvEx) { try { File.Copy(tmp, dataPath, true); File.Delete(tmp); } catch (Exception copyEx) { AppendLog(logPath, "Move/copy failed: " + mvEx.Message + " " + copyEx.Message); return; } }
                AppendLog(logPath, "SaveGameData success: " + dataPath + " (" + DateTime.Now.ToString("O") + ")");
            }
            catch (Exception ex) { AppendLog(logPath, "SaveGameData exception: " + ex.Message); }
        }

        public string GetConfigFilePath() => Path.Combine(GetAppDataDir(), CONFIG_FILE);
        public string GetDataFilePath() => Path.Combine(GetAppDataDir(), DATA_FILE);
        public string GetAppDataDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLauncherPro");

        private static void AppendLog(string path, string text) { try { File.AppendAllText(path, text + Environment.NewLine); } catch { } }
    }
}
