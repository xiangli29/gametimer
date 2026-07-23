using System;

namespace GameLauncherPro.ViewModels
{
    public sealed class PlaySessionViewModel
    {
        public PlaySessionViewModel(string gameName, DateTime startedAt, DateTime endedAt, int durationSeconds)
        {
            GameName = gameName;
            StartedAt = startedAt.ToString("yyyy-MM-dd HH:mm");
            EndedAt = endedAt.ToString("yyyy-MM-dd HH:mm");
            Duration = FormatDuration(durationSeconds);
        }

        public string GameName { get; }
        public string StartedAt { get; }
        public string EndedAt { get; }
        public string Duration { get; }

        private static string FormatDuration(int seconds)
        {
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;
            var remainingSeconds = seconds % 60;
            return $"{hours:00}:{minutes:00}:{remainingSeconds:00}";
        }
    }
}
