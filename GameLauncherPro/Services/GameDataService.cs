using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameLauncherPro.Services
{
    public enum AutoRefreshModeEnum { Manual = 0, AutoOnAC = 1, Always = 2 }

    public class GameDataService
    {
        public const string StatusNotStarted = "未开始";
        public const string StatusPlaying = "游玩中";
        public const string StatusCompleted = "已通关";

        public static readonly IReadOnlyList<string> GameStatuses =
            new[] { StatusNotStarted, StatusPlaying, StatusCompleted };

        private const string ConfigFile = "config.json";
        private const string DataFile = "game_play_time.json";

        public object DataLock { get; } = new object();
        public string GameRootDir { get; set; } = "";
        public AutoRefreshModeEnum AutoRefreshMode { get; set; } = AutoRefreshModeEnum.Manual;
        public bool AutoRefreshCharts { get; set; }
        public bool StrongPowerSaving { get; set; }
        public bool DarkMode { get; set; }
        public int CheckInCount { get; set; }
        public List<TagDefinition> TagCatalog { get; private set; } = new();
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
                NormalizeGameData(GameData);
                MergeGameTagsIntoCatalog();
            }
        }

        public IReadOnlyList<TagDefinition> GetTagCatalogSnapshot()
        {
            lock (DataLock)
            {
                return TagCatalog.Select(CloneTagDefinition).ToList();
            }
        }

        public bool AddTagToCatalog(string? value, out TagDefinition tag)
        {
            var tagName = NormalizeTag(value);
            if (string.IsNullOrEmpty(tagName))
            {
                tag = new TagDefinition();
                return false;
            }

            lock (DataLock)
            {
                var existing = TagCatalog.FirstOrDefault(item =>
                    string.Equals(item.name, tagName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    tag = CloneTagDefinition(existing);
                    return false;
                }

                tag = new TagDefinition
                {
                    name = tagName,
                    color = GetNextTagColor(TagCatalog.Select(item => item.color))
                };
                TagCatalog.Add(tag);
                TagCatalog.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.name, right.name));
                return true;
            }
        }

        public bool RemoveTagFromCatalog(string? value)
        {
            var tag = NormalizeTag(value);
            if (string.IsNullOrEmpty(tag))
            {
                return false;
            }

            lock (DataLock)
            {
                var removed = TagCatalog.RemoveAll(item =>
                    string.Equals(item.name, tag, StringComparison.OrdinalIgnoreCase)) > 0;
                if (!removed)
                {
                    return false;
                }

                foreach (var game in GameData.Values)
                {
                    game.tags.RemoveAll(item => string.Equals(item, tag, StringComparison.OrdinalIgnoreCase));
                }

                return true;
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

                DarkMode = config.TryGetValue("dark_mode", out var darkModeText)
                    && bool.TryParse(darkModeText, out var darkModeValue)
                    && darkModeValue;

                CheckInCount = config.TryGetValue("check_in_count", out var checkInText)
                    && int.TryParse(checkInText, out var checkInValue)
                    ? Math.Max(0, checkInValue)
                    : 0;

                if (config.TryGetValue("tag_catalog", out var tagCatalogText))
                {
                    try
                    {
                        TagCatalog = DeserializeTagCatalog(tagCatalogText);
                    }
                    catch
                    {
                        TagCatalog = new List<TagDefinition>();
                    }
                }
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
                ["strong_power_saving"] = StrongPowerSaving.ToString(),
                ["dark_mode"] = DarkMode.ToString(),
                ["check_in_count"] = Math.Max(0, CheckInCount).ToString(),
                ["tag_catalog"] = JsonSerializer.Serialize(GetTagCatalogSnapshot())
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
                    NormalizeGameData(GameData);
                    MergeGameTagsIntoCatalog();
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

        public static string NormalizeStatus(string? value) =>
            GameStatuses.Contains(value, StringComparer.Ordinal) ? value! : StatusNotStarted;

        public static string NormalizeTag(string? value) => value?.Trim() ?? string.Empty;

        public static List<string> NormalizeTags(IEnumerable<string>? values)
        {
            if (values is null)
            {
                return new List<string>();
            }

            var result = new List<string>();
            foreach (var value in values)
            {
                var tag = NormalizeTag(value);
                if (!string.IsNullOrEmpty(tag)
                    && !result.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(tag);
                }
            }

            result.Sort(StringComparer.CurrentCultureIgnoreCase);
            return result;
        }

        private static void NormalizeGameData(Dictionary<string, GameData> games)
        {
            foreach (var game in games.Values)
            {
                game.exe_paths ??= new List<string>();
                game.screenshot_paths ??= new List<string>();
                game.cover_path ??= string.Empty;
                game.cover_back_path ??= string.Empty;
                game.current_side = game.current_side == "back" ? "back" : "front";
                game.last_play ??= string.Empty;
                game.launch_exe ??= string.Empty;
                game.status = NormalizeStatus(game.status);
                game.tags = NormalizeTags(game.tags);
                game.review ??= string.Empty;
            }
        }

        private void MergeGameTagsIntoCatalog()
        {
            var definitions = TagCatalog.Concat(
                GameData.Values
                    .SelectMany(game => game.tags)
                    .Select(tag => new TagDefinition { name = tag }));
            TagCatalog = NormalizeTagCatalog(definitions);
        }

        private static List<TagDefinition> DeserializeTagCatalog(string text)
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new List<TagDefinition>();
            }

            var definitions = new List<TagDefinition>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    definitions.Add(new TagDefinition { name = element.GetString() ?? string.Empty });
                    continue;
                }

                if (element.ValueKind == JsonValueKind.Object)
                {
                    var name = element.TryGetProperty("name", out var nameProperty)
                        ? nameProperty.GetString()
                        : string.Empty;
                    var color = element.TryGetProperty("color", out var colorProperty)
                        ? colorProperty.GetString()
                        : string.Empty;
                    definitions.Add(new TagDefinition { name = name ?? string.Empty, color = color ?? string.Empty });
                }
            }

            return NormalizeTagCatalog(definitions);
        }

        private static List<TagDefinition> NormalizeTagCatalog(IEnumerable<TagDefinition>? values)
        {
            var result = new List<TagDefinition>();
            var usedColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values ?? Enumerable.Empty<TagDefinition>())
            {
                var name = NormalizeTag(value?.name);
                if (string.IsNullOrEmpty(name)
                    || result.Any(item => string.Equals(item.name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var color = NormalizeTagColor(value?.color);
                if (string.IsNullOrEmpty(color) || !usedColors.Add(color))
                {
                    color = GetNextTagColor(usedColors);
                    usedColors.Add(color);
                }

                result.Add(new TagDefinition { name = name, color = color });
            }

            result.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.name, right.name));
            return result;
        }

        private static TagDefinition CloneTagDefinition(TagDefinition definition) =>
            new() { name = definition.name, color = definition.color };

        private static string NormalizeTagColor(string? value)
        {
            var color = value?.Trim() ?? string.Empty;
            if (color.Length != 7 || color[0] != '#')
            {
                return string.Empty;
            }

            return byte.TryParse(color.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out _)
                && byte.TryParse(color.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out _)
                && byte.TryParse(color.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out _)
                ? color.ToUpperInvariant()
                : string.Empty;
        }

        private static string GetNextTagColor(IEnumerable<string> existingColors)
        {
            var usedColors = new HashSet<string>(existingColors, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 720; index++)
            {
                var hue = (index * 137.508) % 360;
                var color = ColorFromHsl(hue, 0.62, 0.43);
                if (!usedColors.Contains(color))
                {
                    return color;
                }
            }

            return "#3B82F6";
        }

        private static string ColorFromHsl(double hue, double saturation, double lightness)
        {
            var chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
            var segment = hue / 60;
            var second = chroma * (1 - Math.Abs((segment % 2) - 1));
            var match = lightness - (chroma / 2);
            var (red, green, blue) = segment switch
            {
                < 1 => (chroma, second, 0d),
                < 2 => (second, chroma, 0d),
                < 3 => (0d, chroma, second),
                < 4 => (0d, second, chroma),
                < 5 => (second, 0d, chroma),
                _ => (chroma, 0d, second)
            };

            return $"#{(byte)Math.Round((red + match) * 255):X2}{(byte)Math.Round((green + match) * 255):X2}{(byte)Math.Round((blue + match) * 255):X2}";
        }

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
