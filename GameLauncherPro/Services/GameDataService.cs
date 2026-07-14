using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GameLauncherPro.Services
{
    public enum AutoRefreshModeEnum { Manual = 0, AutoOnAC = 1, Always = 2 }

    public class GameDataService
    {
        private const string ConfigFile = "config.json";
        private const string DataFile = "game_play_time.json";

        public object DataLock { get; } = new object();
        public string GameRootDir { get; set; } = "";
        public AutoRefreshModeEnum AutoRefreshMode { get; set; } = AutoRefreshModeEnum.Manual;
        public bool AutoRefreshCharts { get; set; }
        public bool StrongPowerSaving { get; set; }
        public Dictionary<string, GameData> GameData { get; private set; } = new();

        public Dictionary<string, GameData> GetSnapshot()
        {
            lock (DataLock)
            {
                return new Dictionary<string, GameData>(GameData);
            }
        }

        public void ReplaceGameData(Dictionary<string, GameData> gameData)
        {
            lock (DataLock)
            {
                GameData = gameData ?? new Dictionary<string, GameData>();
            }
        }

        public void LoadConfig()
        {
            var configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
            {
                return;
            }

            try
            {
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath));
                if (config is null)
                {
                    return;
                }

                GameRootDir = config.TryGetValue("game_dir", out var dir) ? dir : "";
                if (config.TryGetValue("auto_refresh_mode", out var modeText) && int.TryParse(modeText, out var modeValue))
                {
                    AutoRefreshMode = Enum.IsDefined(typeof(AutoRefreshModeEnum), modeValue)
                        ? (AutoRefreshModeEnum)modeValue
                        : AutoRefreshMode;
                }

                AutoRefreshCharts = config.TryGetValue("auto_refresh_charts", out var autoRefreshText)
                    && bool.TryParse(autoRefreshText, out var autoRefreshValue)
                    && autoRefreshValue;

                StrongPowerSaving = config.TryGetValue("strong_power_saving", out var strongPowerText)
                    && bool.TryParse(strongPowerText, out var strongPowerValue)
                    && strongPowerValue;
            }
            catch
            {
            }
        }

        public void SaveConfig()
        {
            var config = new Dictionary<string, string>
            {
                ["game_dir"] = GameRootDir ?? "",
                ["auto_refresh_charts"] = AutoRefreshCharts.ToString(),
                ["auto_refresh_mode"] = ((int)AutoRefreshMode).ToString(),
                ["strong_power_saving"] = StrongPowerSaving.ToString()
            };

            try
            {
                var path = GetConfigFilePath();
                EnsureDirectoryExists(path);
                File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Save config failed: " + ex.Message, "Error");
            }
        }

        public void LoadGameData()
        {
            var dataPath = GetDataFilePath();
            if (!File.Exists(dataPath))
            {
                lock (DataLock)
                {
                    GameData = new();
                }
                return;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, GameData>>(File.ReadAllText(dataPath)) ?? new();
                lock (DataLock)
                {
                    GameData = loaded;
                }
            }
            catch
            {
                lock (DataLock)
                {
                    GameData = new();
                }
            }
        }

        public void SaveGameData()
        {
            var dataPath = GetDataFilePath();
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "save_log.txt");

            try
            {
                EnsureDirectoryExists(dataPath);

                Dictionary<string, GameData> snapshot;
                lock (DataLock)
                {
                    snapshot = new Dictionary<string, GameData>(GameData);
                }

                var content = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                var tempPath = dataPath + ".tmp";
                File.WriteAllText(tempPath, content);

                try
                {
                    if (File.Exists(dataPath))
                    {
                        File.Delete(dataPath);
                    }

                    File.Move(tempPath, dataPath);
                }
                catch (Exception moveException)
                {
                    try
                    {
                        File.Copy(tempPath, dataPath, overwrite: true);
                        File.Delete(tempPath);
                    }
                    catch (Exception copyException)
                    {
                        AppendLog(logPath, "Move/copy failed: " + moveException.Message + " " + copyException.Message);
                        return;
                    }
                }

                AppendLog(logPath, "SaveGameData success: " + dataPath + " (" + DateTime.Now.ToString("O") + ")");
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "SaveGameData exception: " + ex.Message);
            }
        }

        public string GetConfigFilePath() => Path.Combine(GetAppDataDir(), ConfigFile);
        public string GetDataFilePath() => Path.Combine(GetAppDataDir(), DataFile);
        public string GetAppDataDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLauncherPro");

        private static void EnsureDirectoryExists(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void AppendLog(string path, string text)
        {
            try
            {
                File.AppendAllText(path, text + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
