using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace GameLauncherPro.Services
{
    public static class ThemeService
    {
        private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
        {
            ["CanvasBrush"] = "#F4F6F8",
            ["SidebarBrush"] = "#FFFFFF",
            ["PanelBrush"] = "#FFFFFF",
            ["PanelRaisedBrush"] = "#E9EDF1",
            ["InputBrush"] = "#F8FAFC",
            ["BorderBrush"] = "#D7DEE6",
            ["BorderStrongBrush"] = "#B9C5D2",
            ["TextPrimaryBrush"] = "#1B2430",
            ["TextSecondaryBrush"] = "#475569",
            ["TextMutedBrush"] = "#64748B",
            ["AccentBrush"] = "#3B82F6",
            ["AccentHoverBrush"] = "#2563EB",
            ["InfoBrush"] = "#3B82F6",
            ["NavSelectedBrush"] = "#E0ECFF",
            ["DangerBrush"] = "#E05252",
            ["DangerHoverBrush"] = "#C63D3D",
            ["PlaceholderBrush"] = "#E1E7EE",
            ["ChartBgBrush"] = "#FFFFFF",
            ["ChartAxisBrush"] = "#64748B",
            ["ChartLabelBrush"] = "#1B2430",
            ["SecondaryHoverBrush"] = "#F1F5F9",
            ["DangerBorderBrush"] = "#6B3A3A",
            ["DangerHoverBackgroundBrush"] = "#FFF1F1",
            ["NavHoverBrush"] = "#EFF6FF",
            ["DrawerOverlayBrush"] = "#88000000",
            ["RunningActiveBrush"] = "#3FB950",
            ["RunningIdleBrush"] = "#66737D",
            ["ScoreFilledBrush"] = "#3B82F6",
            ["ScoreEmptyBrush"] = "#E1E7EE",
            ["StatusNotStartedBrush"] = "#64748B",
            ["StatusPlayingBrush"] = "#3B82F6",
            ["StatusCompletedBrush"] = "#22A559"
        };

        private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
        {
            ["CanvasBrush"] = "#0B1220",
            ["SidebarBrush"] = "#101827",
            ["PanelBrush"] = "#162033",
            ["PanelRaisedBrush"] = "#1D2A3E",
            ["InputBrush"] = "#0F1B2D",
            ["BorderBrush"] = "#263850",
            ["BorderStrongBrush"] = "#3A5473",
            ["TextPrimaryBrush"] = "#E6EEF8",
            ["TextSecondaryBrush"] = "#A9BED5",
            ["TextMutedBrush"] = "#7F96B2",
            ["AccentBrush"] = "#60A5FA",
            ["AccentHoverBrush"] = "#93C5FD",
            ["InfoBrush"] = "#60A5FA",
            ["NavSelectedBrush"] = "#193A61",
            ["DangerBrush"] = "#F87171",
            ["DangerHoverBrush"] = "#FCA5A5",
            ["PlaceholderBrush"] = "#243550",
            ["ChartBgBrush"] = "#162033",
            ["ChartAxisBrush"] = "#A9BED5",
            ["ChartLabelBrush"] = "#E6EEF8",
            ["SecondaryHoverBrush"] = "#253852",
            ["DangerBorderBrush"] = "#7F4354",
            ["DangerHoverBackgroundBrush"] = "#3A202B",
            ["NavHoverBrush"] = "#182C49",
            ["DrawerOverlayBrush"] = "#A8000000",
            ["RunningActiveBrush"] = "#4ADE80",
            ["RunningIdleBrush"] = "#64748B",
            ["ScoreFilledBrush"] = "#60A5FA",
            ["ScoreEmptyBrush"] = "#243550",
            ["StatusNotStartedBrush"] = "#94A3B8",
            ["StatusPlayingBrush"] = "#60A5FA",
            ["StatusCompletedBrush"] = "#4ADE80"
        };

        private static FrameworkElement? _resourceOwner;

        public static bool IsDarkMode { get; private set; }

        public static void Apply(FrameworkElement resourceOwner, bool darkMode)
        {
            _resourceOwner = resourceOwner;
            IsDarkMode = darkMode;
            var palette = darkMode ? DarkPalette : LightPalette;

            foreach (var (resourceKey, hexColor) in palette)
            {
                var resourceDictionary = FindResourceDictionary(resourceOwner.Resources, resourceKey);
                if (resourceDictionary is not null
                    && System.Windows.Media.ColorConverter.ConvertFromString(hexColor) is System.Windows.Media.Color color)
                {
                    resourceDictionary[resourceKey] = new SolidColorBrush(color);
                }
            }

            if (resourceOwner is System.Windows.Controls.Control control
                && System.Windows.Media.ColorConverter.ConvertFromString(palette["CanvasBrush"]) is System.Windows.Media.Color canvasColor)
            {
                control.Background = new SolidColorBrush(canvasColor);
            }
        }

        public static System.Windows.Media.Brush? GetBrush(string resourceKey) =>
            _resourceOwner?.TryFindResource(resourceKey) as System.Windows.Media.Brush;

        private static ResourceDictionary? FindResourceDictionary(ResourceDictionary dictionary, string resourceKey)
        {
            if (dictionary.Contains(resourceKey))
            {
                return dictionary;
            }

            foreach (var mergedDictionary in dictionary.MergedDictionaries)
            {
                var match = FindResourceDictionary(mergedDictionary, resourceKey);
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
