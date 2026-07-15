using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using GameLauncherPro.Services;
using MediaBrush = System.Windows.Media.Brush;

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
                Raise(nameof(ScoreTile1Brush));
                Raise(nameof(ScoreTile2Brush));
                Raise(nameof(ScoreTile3Brush));
                Raise(nameof(ScoreTile4Brush));
                Raise(nameof(ScoreTile5Brush));
                Raise(nameof(ScoreTile6Brush));
                Raise(nameof(ScoreTile7Brush));
                Raise(nameof(ScoreTile8Brush));
                Raise(nameof(ScoreTile9Brush));
                Raise(nameof(ScoreTile10Brush));
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

        private string _status = GameDataService.StatusNotStarted;
        public string Status
        {
            get => _status;
            set
            {
                _status = GameDataService.NormalizeStatus(value);
                Raise(nameof(Status));
                Raise(nameof(StatusBrush));
            }
        }

        public MediaBrush StatusBrush => Status switch
        {
            GameDataService.StatusPlaying => ThemeService.GetBrush("StatusPlayingBrush") ?? System.Windows.Media.Brushes.DodgerBlue,
            GameDataService.StatusCompleted => ThemeService.GetBrush("StatusCompletedBrush") ?? System.Windows.Media.Brushes.SeaGreen,
            _ => ThemeService.GetBrush("StatusNotStartedBrush") ?? System.Windows.Media.Brushes.SlateGray
        };

        public ObservableCollection<string> Tags { get; } = new();
        public ObservableCollection<TagDisplayViewModel> CardTags { get; } = new();

        private string _review = "";
        public string Review { get => _review; set { _review = value ?? ""; Raise(nameof(Review)); } }

        public MediaBrush ScoreTile1Brush => GetScoreTileBrush(1);
        public MediaBrush ScoreTile2Brush => GetScoreTileBrush(2);
        public MediaBrush ScoreTile3Brush => GetScoreTileBrush(3);
        public MediaBrush ScoreTile4Brush => GetScoreTileBrush(4);
        public MediaBrush ScoreTile5Brush => GetScoreTileBrush(5);
        public MediaBrush ScoreTile6Brush => GetScoreTileBrush(6);
        public MediaBrush ScoreTile7Brush => GetScoreTileBrush(7);
        public MediaBrush ScoreTile8Brush => GetScoreTileBrush(8);
        public MediaBrush ScoreTile9Brush => GetScoreTileBrush(9);
        public MediaBrush ScoreTile10Brush => GetScoreTileBrush(10);

        public void RefreshThemeDependentValues()
        {
            Raise(nameof(ScoreTile1Brush));
            Raise(nameof(ScoreTile2Brush));
            Raise(nameof(ScoreTile3Brush));
            Raise(nameof(ScoreTile4Brush));
            Raise(nameof(ScoreTile5Brush));
            Raise(nameof(ScoreTile6Brush));
            Raise(nameof(ScoreTile7Brush));
            Raise(nameof(ScoreTile8Brush));
            Raise(nameof(ScoreTile9Brush));
            Raise(nameof(ScoreTile10Brush));
            Raise(nameof(StatusBrush));
            foreach (var tag in CardTags)
            {
                tag.RefreshTheme();
            }
        }

        public ObservableCollection<ScreenshotViewModel> Screenshots { get; } = new();

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

        public void UpdateFromGameData(
            string name,
            GameLauncherPro.GameData data,
            IReadOnlyList<GameLauncherPro.TagDefinition> tagDefinitions)
        {
            Name = name;
            TotalSeconds = data.total_seconds;
            LastPlay = data.last_play ?? "";
            Score = data.score;
            Status = data.status;
            Review = data.review ?? "";
            UpdateTags(data.tags, tagDefinitions);
            FrontImagePath = data.cover_path ?? "";
            BackImagePath = data.cover_back_path ?? "";
            CurrentSide = string.IsNullOrWhiteSpace(data.current_side) ? "front" : data.current_side;
            LaunchExe = data.launch_exe ?? "";
            UpdateScreenshots(data.screenshot_paths);

            var paths = data.exe_paths is null ? new List<string>() : new List<string>(data.exe_paths);
            if (!string.IsNullOrWhiteSpace(data.launch_exe) && !paths.Contains(data.launch_exe))
            {
                paths.Insert(0, data.launch_exe);
            }

            ExePaths = paths;
            var launchIndex = ExePaths.IndexOf(data.launch_exe ?? "");
            SelectedExeIndex = launchIndex >= 0 ? launchIndex : 0;
        }

        public void UpdateTags(
            IEnumerable<string>? tags,
            IReadOnlyList<GameLauncherPro.TagDefinition> tagDefinitions)
        {
            Tags.Clear();
            CardTags.Clear();
            if (tags is null)
            {
                return;
            }

            var normalized = GameDataService.NormalizeTags(tags);
            var definitions = tagDefinitions.ToDictionary(
                definition => definition.name,
                definition => definition,
                StringComparer.OrdinalIgnoreCase);
            foreach (var tag in normalized)
            {
                Tags.Add(tag);
                if (definitions.TryGetValue(tag, out var definition))
                {
                    CardTags.Add(new TagDisplayViewModel(definition));
                }
                else
                {
                    CardTags.Add(new TagDisplayViewModel(new GameLauncherPro.TagDefinition
                    {
                        name = tag,
                        color = "#64748B"
                    }));
                }
            }

            CardTags.Add(TagDisplayViewModel.CreateOverflow());
        }

        private void UpdateScreenshots(IEnumerable<string>? screenshotPaths)
        {
            Screenshots.Clear();
            if (screenshotPaths is null)
            {
                return;
            }

            foreach (var path in screenshotPaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Screenshots.Add(new ScreenshotViewModel(path));
                }
            }
        }

        private MediaBrush GetScoreTileBrush(int tileScore) =>
            ThemeService.GetBrush(Score >= tileScore ? "ScoreFilledBrush" : "ScoreEmptyBrush")
            ?? System.Windows.Media.Brushes.Transparent;

        private static string FormatTime(int seconds)
        {
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;
            var remainingSeconds = seconds % 60;
            return $"{hours:00}小时{minutes:00}分钟{remainingSeconds:00}秒";
        }
    }
}
