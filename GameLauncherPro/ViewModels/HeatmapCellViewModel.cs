using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace GameLauncherPro.ViewModels
{
    public sealed class HeatmapCellViewModel
    {
        public string Label { get; init; } = "";
        public string ToolTip { get; init; } = "";
        public MediaBrush Background { get; init; } = MediaBrushes.Transparent;
        public MediaBrush Foreground { get; init; } = MediaBrushes.Transparent;
        public bool IsInRange { get; init; }
    }
}
