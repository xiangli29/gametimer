using System;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLauncherPro.ViewModels
{
    public class GameViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string Name { get; set; } = "";

        private int total_seconds;
        public int TotalSeconds { get => total_seconds; set { total_seconds = value; Raise(nameof(TotalSeconds)); Raise(nameof(DisplayTotal)); Raise(nameof(DisplayPlayTime)); } }

        public string DisplayTotal => FormatTime(TotalSeconds);

        // 卡片简短时长：12h 30m
        public string DisplayPlayTime
        {
            get
            {
                if (TotalSeconds <= 0) return "";
                int h = TotalSeconds / 3600;
                int m = (TotalSeconds % 3600) / 60;
                if (h > 0) return $"{h}h {m}m";
                return $"{m}m";
            }
        }

        private string last_play = "";
        public string LastPlay { get => last_play; set { last_play = value; Raise(nameof(LastPlay)); } }

        private int score;
        public int Score { get => score; set { score = value; Raise(nameof(Score)); Raise(nameof(StarsDisplay)); } }

        // 评分星级：★★★★★☆☆☆☆☆
        public string StarsDisplay
        {
            get
            {
                if (Score <= 0) return "";
                var stars = new System.Text.StringBuilder(10);
                for (int i = 0; i < 10; i++)
                    stars.Append(i < Score ? '\u2605' : '\u2606');
                return stars.ToString();
            }
        }

        private ImageSource? frontImage;
        public ImageSource? FrontImage { get => frontImage; set { frontImage = value; Raise(nameof(FrontImage)); } }

        private ImageSource? backImage;
        public ImageSource? BackImage { get => backImage; set { backImage = value; Raise(nameof(BackImage)); } }

        private string currentSide = "front";
        public string CurrentSide { get => currentSide; set { currentSide = value; Raise(nameof(CurrentSide)); Raise(nameof(IsFrontVisible)); Raise(nameof(IsBackVisible)); } }

        public bool IsFrontVisible => CurrentSide == "front";
        public bool IsBackVisible => CurrentSide == "back";

        public string LaunchExe { get; set; } = "";

        public void UpdateFromGameData(string name, GameLauncherPro.GameData data)
        {
            if (data == null) return;
            Name = name;
            TotalSeconds = data.total_seconds;
            LastPlay = data.last_play ?? "";
            Score = data.score;
            CurrentSide = string.IsNullOrEmpty(data.current_side) ? "front" : data.current_side;
            LaunchExe = data.launch_exe ?? "";
        }

        private string FormatTime(int seconds)
        {
            int h = seconds / 3600;
            int m = (seconds % 3600) / 60;
            int s = seconds % 60;
            return $"{h:00}小时{m:00}分钟{s:00}秒";
        }
    }
}
