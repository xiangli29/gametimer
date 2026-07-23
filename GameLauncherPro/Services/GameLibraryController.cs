using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using GameLauncherPro.ViewModels;

namespace GameLauncherPro.Services
{
    public enum GameSortOption
    {
        Default = 0,
        PlayTimeDesc = 1,
        LastPlayedDesc = 2,
        ScoreDesc = 3
    }

    public sealed class GameLibraryController
    {
        private readonly GameDataService _data;
        private readonly ImageCacheService _imageCache;
        private CancellationTokenSource? _imageLoadCts;

        public GameLibraryController(GameDataService data, ImageCacheService imageCache)
        {
            _data = data;
            _imageCache = imageCache;
            Games = new ObservableCollection<GameViewModel>();
            GamesView = CollectionViewSource.GetDefaultView(Games);
            GamesView.Filter = FilterGame;
        }

        public ObservableCollection<GameViewModel> Games { get; }

        public ICollectionView GamesView { get; }

        public string SearchText { get; private set; } = string.Empty;

        public GameSortOption SortOption { get; private set; } = GameSortOption.LastPlayedDesc;

        public string StatusFilter { get; private set; } = string.Empty;

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            List<KeyValuePair<string, GameData>> snapshot;
            lock (_data.DataLock)
            {
                snapshot = _data.GameData.ToList();
            }
            var tagDefinitions = _data.GetTagCatalogSnapshot();

            var existing = Games.ToDictionary(game => game.Name, StringComparer.OrdinalIgnoreCase);
            var incomingNames = new HashSet<string>(snapshot.Select(item => item.Key), StringComparer.OrdinalIgnoreCase);

            for (var index = Games.Count - 1; index >= 0; index--)
            {
                if (!incomingNames.Contains(Games[index].Name))
                {
                    Games.RemoveAt(index);
                }
            }

            var imagesToLoad = new List<(GameViewModel ViewModel, GameData Data)>(snapshot.Count);
            foreach (var (name, data) in snapshot)
            {
                if (!existing.TryGetValue(name, out var viewModel))
                {
                    viewModel = new GameViewModel();
                    Games.Add(viewModel);
                }

                viewModel.UpdateFromGameData(name, data, tagDefinitions);
                imagesToLoad.Add((viewModel, data));
            }

            ApplyView();
            QueueImageLoad(imagesToLoad, cancellationToken);
            await Task.CompletedTask;
        }

        public void ApplyViewState(string? searchText, GameSortOption sortOption, string? statusFilter)
        {
            SearchText = searchText?.Trim() ?? string.Empty;
            SortOption = sortOption;
            StatusFilter = GameDataService.GameStatuses.Contains(statusFilter, StringComparer.Ordinal)
                ? statusFilter!
                : string.Empty;
            ApplyView();
        }

        public void RefreshTheme()
        {
            foreach (var game in Games)
            {
                game.RefreshThemeDependentValues();
            }
        }

        public void ToggleCurrentSide(string gameName)
        {
            lock (_data.DataLock)
            {
                var data = GetOrCreateGameData(gameName);
                data.current_side = data.current_side == "back" ? "front" : "back";
            }

            if (FindViewModel(gameName) is { } vm)
            {
                vm.CurrentSide = vm.CurrentSide == "back" ? "front" : "back";
            }
        }

        public void SetCover(string gameName, string path, bool setFront)
        {
            lock (_data.DataLock)
            {
                var data = GetOrCreateGameData(gameName);
                if (setFront)
                {
                    data.cover_path = path;
                }
                else
                {
                    data.cover_back_path = path;
                }
            }
        }

        public void ToggleAllCurrentSides()
        {
            lock (_data.DataLock)
            {
                foreach (var data in _data.GameData.Values)
                {
                    data.current_side = data.current_side == "back" ? "front" : "back";
                }
            }

            foreach (var game in Games)
            {
                game.CurrentSide = game.CurrentSide == "back" ? "front" : "back";
            }
        }

        public void AddScreenshots(string gameName, IEnumerable<string> paths)
        {
            lock (_data.DataLock)
            {
                var screenshots = GetOrCreateGameData(gameName).screenshot_paths;
                foreach (var path in paths)
                {
                    if (!string.IsNullOrWhiteSpace(path)
                        && !screenshots.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        screenshots.Add(path);
                    }
                }
            }
        }

        public void RemoveScreenshot(string gameName, string path)
        {
            lock (_data.DataLock)
            {
                var screenshots = GetOrCreateGameData(gameName).screenshot_paths;
                screenshots.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void SetScore(string gameName, int score)
        {
            lock (_data.DataLock)
            {
                GetOrCreateGameData(gameName).score = score;
            }

            if (FindViewModel(gameName) is { } vm)
            {
                vm.Score = score;
            }
        }

        public void SetStatus(string gameName, string? status)
        {
            var normalized = GameDataService.NormalizeStatus(status);
            lock (_data.DataLock)
            {
                GetOrCreateGameData(gameName).status = normalized;
            }

            if (FindViewModel(gameName) is { } vm)
            {
                vm.Status = normalized;
            }
        }

        public bool ToggleTag(string gameName, string? tag)
        {
            var normalized = GameDataService.NormalizeTag(tag);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            List<string> tags;
            var isSelected = false;
            lock (_data.DataLock)
            {
                var game = GetOrCreateGameData(gameName);
                var existing = game.tags.FirstOrDefault(item =>
                    string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    game.tags.Remove(existing);
                }
                else
                {
                    game.tags.Add(normalized);
                    game.tags = GameDataService.NormalizeTags(game.tags);
                    isSelected = true;
                }

                tags = game.tags.ToList();
            }

            if (FindViewModel(gameName) is { } vm)
            {
                vm.UpdateTags(tags, _data.GetTagCatalogSnapshot());
            }

            return isSelected;
        }

        public void SetReview(string gameName, string? review)
        {
            var normalized = review?.Trim() ?? string.Empty;
            lock (_data.DataLock)
            {
                GetOrCreateGameData(gameName).review = normalized;
            }

            if (FindViewModel(gameName) is { } vm)
            {
                vm.Review = normalized;
            }
        }

        public void SetTotalSeconds(string gameName, int totalSeconds, DateTime lastPlayedAt)
        {
            lock (_data.DataLock)
            {
                var data = GetOrCreateGameData(gameName);
                data.total_seconds = totalSeconds;
                data.last_play = lastPlayedAt.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        public bool TrySetLaunchExe(string gameName, string launchExe, out string conflictGameName)
        {
            return _data.TryAssignLaunchExecutable(gameName, launchExe, out conflictGameName);
        }

        public void SetLaunchExe(string gameName, string launchExe)
        {
            TrySetLaunchExe(gameName, launchExe, out _);
        }

        public void AddOrUpdateGame(string gameName, string exePath)
        {
            SetLaunchExe(gameName, exePath);
        }

        public bool DeleteGame(string gameName)
        {
            lock (_data.DataLock)
            {
                return _data.GameData.Remove(gameName);
            }
        }

        public bool RenameGame(string oldName, string newName)
        {
            lock (_data.DataLock)
            {
                if (_data.GameData.ContainsKey(newName))
                {
                    return false;
                }

                if (_data.GameData.Remove(oldName, out var data))
                {
                    _data.GameData[newName] = data;
                    return true;
                }
            }

            return false;
        }

        public bool TryGetLaunchExe(string gameName, out string exePath)
        {
            lock (_data.DataLock)
            {
                if (_data.GameData.TryGetValue(gameName, out var data))
                {
                    exePath = data.launch_exe;
                    return true;
                }
            }

            exePath = string.Empty;
            return false;
        }

        private void QueueImageLoad(IEnumerable<(GameViewModel ViewModel, GameData Data)> imagesToLoad, CancellationToken cancellationToken)
        {
            _imageLoadCts?.Cancel();
            _imageLoadCts?.Dispose();
            _imageLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _imageLoadCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(
                        imagesToLoad,
                        new ParallelOptions
                        {
                            CancellationToken = token,
                            MaxDegreeOfParallelism = 4
                        },
                        async (item, ct) => await _imageCache.LoadImagesAsync(item.ViewModel, item.Data, ct));
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }

        private void ApplyView()
        {
            using (GamesView.DeferRefresh())
            {
                GamesView.SortDescriptions.Clear();
                switch (SortOption)
                {
                    case GameSortOption.PlayTimeDesc:
                        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameViewModel.TotalSeconds), ListSortDirection.Descending));
                        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameViewModel.Name), ListSortDirection.Ascending));
                        break;
                    case GameSortOption.LastPlayedDesc:
                        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameViewModel.LastPlay), ListSortDirection.Descending));
                        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameViewModel.Name), ListSortDirection.Ascending));
                        break;
                    case GameSortOption.ScoreDesc:
                        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameViewModel.Score), ListSortDirection.Descending));
                        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameViewModel.Name), ListSortDirection.Ascending));
                        break;
                    case GameSortOption.Default:
                    default:
                        break;
                }
            }
        }

        private bool FilterGame(object item)
        {
            if (item is not GameViewModel game)
            {
                return false;
            }

            var matchesStatus = string.IsNullOrEmpty(StatusFilter)
                || string.Equals(game.Status, StatusFilter, StringComparison.Ordinal);
            if (!matchesStatus)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(SearchText)
                || game.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || game.Tags.Any(tag => tag.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        private GameViewModel? FindViewModel(string gameName)
        {
            return Games.FirstOrDefault(game => string.Equals(game.Name, gameName, StringComparison.OrdinalIgnoreCase));
        }

        private GameData GetOrCreateGameData(string gameName)
        {
            if (!_data.GameData.TryGetValue(gameName, out var data))
            {
                data = new GameData { game_id = Guid.NewGuid().ToString("N") };
                _data.GameData[gameName] = data;
            }

            return data;
        }
    }
}
