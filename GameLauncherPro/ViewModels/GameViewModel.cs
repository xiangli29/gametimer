using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;

namespace GameLauncherPro.ViewModels
{
    public class GameViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private string _name = "";
        public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }

        private int _totalSeconds;
        public int TotalSeconds
        {
            get => _totalSeconds;
            set
            {
                _totalSeconds = value;
                Raise(nameof(TotalSeconds));
                Raise(nameof(DisplayTotal));
                Raise(nameof(DisplayPlayTime));
            }
        }

        public string DisplayTotal => FormatTime(TotalSeconds);

        public string DisplayPlayTime
        {
            get
            {
                if (TotalSeconds <= 0) return "";
                var hours = TotalSeconds / 3600;
                var minutes = (TotalSeconds % 3600) / 60;
                return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
            }
        }

        private string _lastPlay = "";
        public string LastPlay { get => _lastPlay; set { _lastPlay = value; Raise(nameof(LastPlay)); } }

        private int _score;
        public int Score
        {
            get => _score;
            set
            {
                _score = value;
                Raise(nameof(Score));
                Raise(nameof(StarsDisplay));
            }
        }

        public string StarsDisplay
        {
            get
            {
                if (Score <= 0) return "";
                var stars = new System.Text.StringBuilder(10);
                for (var index = 0; index < 10; index++)
                {
                    stars.Append(index < Score ? '\u2605' : '\u2606');
                }

                return stars.ToString();
            }
        }

        private ImageSource? _frontImage;
        public ImageSource? FrontImage { get => _frontImage; set { _frontImage = value; Raise(nameof(FrontImage)); } }

        private ImageSource? _backImage;
        public ImageSource? BackImage { get => _backImage; set { _backImage = value; Raise(nameof(BackImage)); } }

        private string _frontImagePath = "";
        public string FrontImagePath
        {
            get => _frontImagePath;
            set
            {
                if (_frontImagePath == value) return;
                _frontImagePath = value;
                FrontImage = null;
                Raise(nameof(FrontImagePath));
            }
        }

        private string _backImagePath = "";
        public string BackImagePath
        {
            get => _backImagePath;
            set
            {
                if (_backImagePath == value) return;
                _backImagePath = value;
                BackImage = null;
                Raise(nameof(BackImagePath));
            }
        }

        private string _currentSide = "front";
        public string CurrentSide
        {
            get => _currentSide;
            set
            {
                _currentSide = value;
                Raise(nameof(CurrentSide));
                Raise(nameof(IsFrontVisible));
                Raise(nameof(IsBackVisible));
            }
        }

        public bool IsFrontVisible => CurrentSide == "front";
        public bool IsBackVisible => CurrentSide == "back";

        private string _launchExe = "";
        public string LaunchExe
        {
            get => _launchExe;
            set
            {
                _launchExe = value;
                Raise(nameof(LaunchExe));
                Raise(nameof(LaunchExeDisplay));
            }
        }

        private List<string> _exePaths = new();
        public List<string> ExePaths
        {
            get => _exePaths;
            set
            {
                _exePaths = value;
                Raise(nameof(ExePaths));
                Raise(nameof(LaunchExeDisplay));
            }
        }

        private int _selectedExeIndex;
        public int SelectedExeIndex
        {
            get => _selectedExeIndex;
            set
            {
                _selectedExeIndex = value;
                Raise(nameof(SelectedExeIndex));
                Raise(nameof(LaunchExeDisplay));
            }
        }

        public string LaunchExeDisplay
        {
            get
            {
                if (ExePaths.Count == 0) return "";
                var index = SelectedExeIndex;
                if (index < 0 || index >= ExePaths.Count)
                {
                    index = 0;
                }

                var path = ExePaths[index];
                return System.IO.Path.GetFileName(path) + "\n" + path;
            }
        }

        public void UpdateFromGameData(string name, GameLauncherPro.GameData data)
        {
            Name = name;
            TotalSeconds = data.total_seconds;
            LastPlay = data.last_play ?? "";
            Score = data.score;
            FrontImagePath = data.cover_path ?? "";
            BackImagePath = data.cover_back_path ?? "";
            CurrentSide = string.IsNullOrWhiteSpace(data.current_side) ? "front" : data.current_side;
            LaunchExe = data.launch_exe ?? "";

            var paths = data.exe_paths is null ? new List<string>() : new List<string>(data.exe_paths);
            if (!string.IsNullOrWhiteSpace(data.launch_exe) && !paths.Contains(data.launch_exe))
            {
                paths.Insert(0, data.launch_exe);
            }

            ExePaths = paths;
            var launchIndex = ExePaths.IndexOf(data.launch_exe ?? "");
            SelectedExeIndex = launchIndex >= 0 ? launchIndex : 0;
        }

        private static string FormatTime(int seconds)
        {
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;
            var remainingSeconds = seconds % 60;
            return $"{hours:00}小时{minutes:00}分钟{remainingSeconds:00}秒";
        }
    }
}
