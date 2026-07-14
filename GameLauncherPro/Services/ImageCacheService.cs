using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using GameLauncherPro.ViewModels;

namespace GameLauncherPro.Services
{
    public sealed class ImageCacheService : IDisposable
    {
        private const int DecodeWidth = 253;
        private const int DecodeHeight = 326;
        private const int MaxCachedImages = 256;

        private readonly string _thumbnailDirectory;
        private readonly SemaphoreSlim _loadSemaphore = new(4);
        private readonly object _cacheLock = new();
        private readonly Dictionary<string, BitmapSource> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _cacheOrder = new();

        public ImageCacheService(string appDataDirectory)
        {
            _thumbnailDirectory = Path.Combine(appDataDirectory, "thumbnails");
            Directory.CreateDirectory(appDataDirectory);
            Directory.CreateDirectory(_thumbnailDirectory);
        }

        public async Task LoadImagesAsync(GameViewModel viewModel, GameData data, CancellationToken cancellationToken)
        {
            var frontPath = data.cover_path ?? string.Empty;
            var backPath = data.cover_back_path ?? string.Empty;

            await LoadSingleImageAsync(viewModel, frontPath, isFront: true, cancellationToken);
            await LoadSingleImageAsync(viewModel, backPath, isFront: false, cancellationToken);
            await LoadScreenshotsAsync(viewModel, cancellationToken);
        }

        public void Dispose()
        {
            _loadSemaphore.Dispose();
        }

        private async Task LoadSingleImageAsync(GameViewModel viewModel, string path, bool isFront, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (isFront)
                    {
                        if (string.Equals(viewModel.FrontImagePath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            viewModel.FrontImage = null;
                        }
                    }
                    else if (string.Equals(viewModel.BackImagePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        viewModel.BackImage = null;
                    }
                });
                return;
            }

            var image = await LoadImageAsync(path, cancellationToken);
            if (image is null)
            {
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (isFront)
                {
                    if (string.Equals(viewModel.FrontImagePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        viewModel.FrontImage = image;
                    }
                }
                else if (string.Equals(viewModel.BackImagePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.BackImage = image;
                }
            });
        }

        private async Task LoadScreenshotsAsync(GameViewModel viewModel, CancellationToken cancellationToken)
        {
            foreach (var screenshot in viewModel.Screenshots.ToArray())
            {
                if (string.IsNullOrWhiteSpace(screenshot.ImagePath) || !File.Exists(screenshot.ImagePath))
                {
                    continue;
                }

                var image = await LoadImageAsync(screenshot.ImagePath, cancellationToken);
                if (image is null)
                {
                    continue;
                }

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    screenshot.Image = image;
                });
            }
        }

        private async Task<BitmapSource?> LoadImageAsync(string originalPath, CancellationToken cancellationToken)
        {
            if (TryGetFromMemoryCache(originalPath, out var cached))
            {
                return cached;
            }

            await _loadSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (TryGetFromMemoryCache(originalPath, out cached))
                {
                    return cached;
                }

                var thumbnailPath = GetThumbnailPath(originalPath);
                BitmapSource image;
                if (File.Exists(thumbnailPath))
                {
                    image = CreateBitmap(thumbnailPath, decodePixelWidth: 0, decodePixelHeight: 0);
                }
                else
                {
                    image = CreateBitmap(originalPath, DecodeWidth, DecodeHeight);
                    SaveThumbnail(thumbnailPath, image);
                }

                AddToMemoryCache(originalPath, image);
                return image;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        private static BitmapSource CreateBitmap(string path, int decodePixelWidth, int decodePixelHeight)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            if (decodePixelWidth > 0)
            {
                bitmap.DecodePixelWidth = decodePixelWidth;
            }
            if (decodePixelHeight > 0)
            {
                bitmap.DecodePixelHeight = decodePixelHeight;
            }
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private void SaveThumbnail(string thumbnailPath, BitmapSource image)
        {
            try
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Open(thumbnailPath, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(stream);
            }
            catch
            {
            }
        }

        private string GetThumbnailPath(string originalPath)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(originalPath.ToLowerInvariant()));
            var hashText = Convert.ToHexString(hash).ToLowerInvariant();
            var fileName = Path.GetFileNameWithoutExtension(originalPath);
            return Path.Combine(_thumbnailDirectory, $"{fileName}_{hashText}.jpg");
        }

        private bool TryGetFromMemoryCache(string key, out BitmapSource image)
        {
            lock (_cacheLock)
            {
                if (_memoryCache.TryGetValue(key, out image!))
                {
                    TouchCacheEntry(key);
                    return true;
                }
            }

            image = null!;
            return false;
        }

        private void AddToMemoryCache(string key, BitmapSource image)
        {
            lock (_cacheLock)
            {
                _memoryCache[key] = image;
                TouchCacheEntry(key);

                while (_cacheOrder.Count > MaxCachedImages)
                {
                    var oldest = _cacheOrder.First;
                    if (oldest is null)
                    {
                        break;
                    }

                    _cacheOrder.RemoveFirst();
                    _memoryCache.Remove(oldest.Value);
                }
            }
        }

        private void TouchCacheEntry(string key)
        {
            var current = _cacheOrder.Find(key);
            if (current is not null)
            {
                _cacheOrder.Remove(current);
            }

            _cacheOrder.AddLast(key);
        }
    }
}
