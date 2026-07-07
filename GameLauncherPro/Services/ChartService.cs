using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GameLauncherPro.Services
{
    public sealed class ChartService
    {
        public ChartSnapshot BuildSnapshot(IEnumerable<KeyValuePair<string, GameData>> games)
        {
            var orderedGames = games.OrderByDescending(game => game.Value.total_seconds).ToList();
            var labels = orderedGames.Select(game => game.Key).ToArray();
            var values = orderedGames.Select(game => (double)game.Value.total_seconds).ToArray();

            var rankText = new StringBuilder();
            var totalSeconds = orderedGames.Sum(game => game.Value.total_seconds);
            rankText.AppendLine($"总时长：{FormatTime(totalSeconds)}");
            rankText.AppendLine("========================");

            for (var index = 0; index < orderedGames.Count; index++)
            {
                var item = orderedGames[index];
                rankText.AppendLine($"{index + 1}. {item.Key}");
                rankText.AppendLine($"   {FormatTime(item.Value.total_seconds)}");
                rankText.AppendLine();
            }

            return new ChartSnapshot(orderedGames, labels, values, rankText.ToString());
        }

        public string BuildRecordText(string gameRootDir, IEnumerable<KeyValuePair<string, GameData>> games)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"统计时间：{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"监控目录：{gameRootDir}");
            builder.AppendLine();

            foreach (var (name, data) in games)
            {
                builder.AppendLine($"游戏：{name}");
                builder.AppendLine($"总时长：{FormatTime(data.total_seconds)}");
                builder.AppendLine($"最后游玩：{data.last_play}");
                builder.AppendLine($"启动路径：{data.launch_exe}");
                builder.AppendLine(new string('-', 50));
                builder.AppendLine();
            }

            return builder.ToString();
        }

        public string FormatTime(int seconds)
        {
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;
            var remainingSeconds = seconds % 60;
            return $"{hours:00}小时{minutes:00}分钟{remainingSeconds:00}秒";
        }
    }

    public sealed record ChartSnapshot(
        IReadOnlyList<KeyValuePair<string, GameData>> OrderedGames,
        string[] Labels,
        double[] Values,
        string RankText);
}
