using System;
using System.ComponentModel;
using System.Windows.Media;
using GameLauncherPro.Services;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace GameLauncherPro.ViewModels
{
    public class TagDisplayViewModel : INotifyPropertyChanged
    {
        private int _hiddenCount;
        private MediaBrush _backgroundBrush = MediaBrushes.Transparent;
        private MediaBrush _borderBrush = MediaBrushes.Transparent;
        private MediaBrush _foregroundBrush = MediaBrushes.Black;

        public TagDisplayViewModel(TagDefinition definition)
        {
            Name = definition.name;
            Color = definition.color;
            RefreshTheme();
        }

        protected TagDisplayViewModel()
        {
            IsOverflow = true;
            RefreshTheme();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; } = "";

        public string Color { get; } = "";

        public bool IsOverflow { get; }

        public int HiddenCount
        {
            get => _hiddenCount;
            set
            {
                if (_hiddenCount == value)
                {
                    return;
                }

                _hiddenCount = value;
                Raise(nameof(HiddenCount));
                Raise(nameof(DisplayText));
                Raise(nameof(ToolTip));
            }
        }

        public string DisplayText => IsOverflow ? $"+{HiddenCount}" : Name;

        public string ToolTip => IsOverflow ? $"还有 {HiddenCount} 个标签" : Name;

        public MediaBrush BackgroundBrush
        {
            get => _backgroundBrush;
            private set
            {
                _backgroundBrush = value;
                Raise(nameof(BackgroundBrush));
            }
        }

        public MediaBrush BorderBrush
        {
            get => _borderBrush;
            private set
            {
                _borderBrush = value;
                Raise(nameof(BorderBrush));
            }
        }

        public MediaBrush ForegroundBrush
        {
            get => _foregroundBrush;
            private set
            {
                _foregroundBrush = value;
                Raise(nameof(ForegroundBrush));
            }
        }

        public static TagDisplayViewModel CreateOverflow() => new();

        public void RefreshTheme()
        {
            if (IsOverflow || !TryParseColor(Color, out var color))
            {
                BackgroundBrush = ThemeService.GetBrush("PanelRaisedBrush") ?? MediaBrushes.LightGray;
                BorderBrush = ThemeService.GetBrush("BorderBrush") ?? MediaBrushes.Gray;
                ForegroundBrush = ThemeService.GetBrush("TextSecondaryBrush") ?? MediaBrushes.DimGray;
                return;
            }

            var backgroundAlpha = ThemeService.IsDarkMode ? (byte)78 : (byte)36;
            BackgroundBrush = new MediaSolidColorBrush(MediaColor.FromArgb(backgroundAlpha, color.R, color.G, color.B));
            BorderBrush = new MediaSolidColorBrush(color);
            ForegroundBrush = new MediaSolidColorBrush(
                ThemeService.IsDarkMode ? Lighten(color, 0.20) : Darken(color, 0.16));
        }

        protected void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private static bool TryParseColor(string value, out MediaColor color)
        {
            color = MediaColors.Transparent;
            if (string.IsNullOrWhiteSpace(value)
                || System.Windows.Media.ColorConverter.ConvertFromString(value) is not MediaColor parsed)
            {
                return false;
            }

            color = parsed;
            return true;
        }

        private static MediaColor Lighten(MediaColor color, double amount) => MediaColor.FromRgb(
            Blend(color.R, 255, amount),
            Blend(color.G, 255, amount),
            Blend(color.B, 255, amount));

        private static MediaColor Darken(MediaColor color, double amount) => MediaColor.FromRgb(
            Blend(color.R, 0, amount),
            Blend(color.G, 0, amount),
            Blend(color.B, 0, amount));

        private static byte Blend(byte value, byte target, double amount) =>
            (byte)Math.Round(value + ((target - value) * amount), MidpointRounding.AwayFromZero);
    }
}
