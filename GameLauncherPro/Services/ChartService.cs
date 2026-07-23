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

        public InsightsSnapshot BuildInsights(IEnumerable<GameDataService.PlaySessionEntry> sessions, System.DateTime now)
        {
            var today = now.Date;
            var rangeStart = today.AddDays(-29);
            var rangeEnd = today.AddDays(1);
            var relevant = sessions
                .Where(session => session.EndedAt > rangeStart && session.StartedAt < rangeEnd)
                .ToList();

            var dailyTotals = new Dictionary<System.DateTime, int>();
            var hourTotals = new Dictionary<(System.DayOfWeek Day, int Hour), int>();
            foreach (var session in relevant)
            {
                AccumulateByDay(session, rangeStart, rangeEnd, dailyTotals);
                AccumulateByHour(session, rangeStart, rangeEnd, hourTotals);
            }

            var calendarStart = rangeStart.AddDays(-((int)(rangeStart.DayOfWeek + 6) % 7));
            var dailyCells = Enumerable.Range(0, 35)
                .Select(offset =>
                {
                    var date = calendarStart.AddDays(offset);
                    var inRange = date >= rangeStart && date <= today;
                    return new DailyHeatmapCell(date, inRange, inRange && dailyTotals.TryGetValue(date, out var seconds) ? seconds : 0);
                })
                .ToList();

            var hourlyCells = Enumerable.Range(0, 7)
                .SelectMany(dayOffset => Enumerable.Range(0, 24)
                    .Select(hour =>
                    {
                        var day = (System.DayOfWeek)((dayOffset + 1) % 7);
                        return new HourHeatmapCell(day, hour, hourTotals.TryGetValue((day, hour), out var seconds) ? seconds : 0);
                    }))
                .ToList();

            return new InsightsSnapshot(
                dailyCells,
                hourlyCells,
                dailyCells.Max(cell => cell.Seconds),
                hourlyCells.Max(cell => cell.Seconds));
        }

        private static void AccumulateByDay(
            GameDataService.PlaySessionEntry session,
            System.DateTime rangeStart,
            System.DateTime rangeEnd,
            IDictionary<System.DateTime, int> totals)
        {
            var cursor = session.StartedAt > rangeStart ? session.StartedAt : rangeStart;
            var end = session.EndedAt < rangeEnd ? session.EndedAt : rangeEnd;
            while (cursor < end)
            {
                var boundary = cursor.Date.AddDays(1);
                var segmentEnd = boundary < end ? boundary : end;
                AddSeconds(totals, cursor.Date, segmentEnd - cursor);
                cursor = segmentEnd;
            }
        }

        private static void AccumulateByHour(
            GameDataService.PlaySessionEntry session,
            System.DateTime rangeStart,
            System.DateTime rangeEnd,
            IDictionary<(System.DayOfWeek Day, int Hour), int> totals)
        {
            var cursor = session.StartedAt > rangeStart ? session.StartedAt : rangeStart;
            var end = session.EndedAt < rangeEnd ? session.EndedAt : rangeEnd;
            while (cursor < end)
            {
                var boundary = cursor.Date.AddHours(cursor.Hour + 1);
                var segmentEnd = boundary < end ? boundary : end;
                AddSeconds(totals, (cursor.DayOfWeek, cursor.Hour), segmentEnd - cursor);
                cursor = segmentEnd;
            }
        }

        private static void AddSeconds<TKey>(IDictionary<TKey, int> totals, TKey key, System.TimeSpan duration) where TKey : notnull
        {
            var seconds = Math.Max(0, (int)duration.TotalSeconds);
            if (seconds == 0)
            {
                return;
            }

            totals[key] = totals.TryGetValue(key, out var existing) ? existing + seconds : seconds;
        }
    }

    public sealed record ChartSnapshot(
        IReadOnlyList<KeyValuePair<string, GameData>> OrderedGames,
        string[] Labels,
        double[] Values,
        string RankText);

    public sealed record DailyHeatmapCell(System.DateTime Date, bool IsInRange, int Seconds);

    public sealed record HourHeatmapCell(System.DayOfWeek Day, int Hour, int Seconds);

    public sealed record InsightsSnapshot(
        IReadOnlyList<DailyHeatmapCell> DailyCells,
        IReadOnlyList<HourHeatmapCell> HourlyCells,
        int MaxDailySeconds,
        int MaxHourlySeconds);
}
