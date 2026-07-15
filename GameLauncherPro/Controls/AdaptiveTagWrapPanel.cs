using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GameLauncherPro.ViewModels;
using WpfPanel = System.Windows.Controls.Panel;
using WpfSize = System.Windows.Size;

namespace GameLauncherPro.Controls
{
    public sealed class AdaptiveTagWrapPanel : WpfPanel
    {
        public static readonly DependencyProperty MaxRowsProperty = DependencyProperty.Register(
            nameof(MaxRows),
            typeof(int),
            typeof(AdaptiveTagWrapPanel),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

        private readonly List<UIElement> _arrangedChildren = new();

        public int MaxRows
        {
            get => (int)GetValue(MaxRowsProperty);
            set => SetValue(MaxRowsProperty, value);
        }

        protected override WpfSize MeasureOverride(WpfSize availableSize)
        {
            var width = double.IsInfinity(availableSize.Width) ? ActualWidth : availableSize.Width;
            if (width <= 0 || double.IsNaN(width))
            {
                width = 1;
            }

            var actualTags = Children
                .Cast<UIElement>()
                .Where(child => GetTagModel(child) is not { IsOverflow: true })
                .ToList();
            var overflow = Children
                .Cast<UIElement>()
                .FirstOrDefault(child => GetTagModel(child) is { IsOverflow: true });

            foreach (var child in actualTags)
            {
                child.Visibility = Visibility.Visible;
                child.Measure(new WpfSize(width, double.PositiveInfinity));
            }

            if (overflow is null)
            {
                SetVisibleChildren(actualTags);
                return GetMeasuredSize(width);
            }

            overflow.Visibility = Visibility.Collapsed;
            overflow.Measure(new WpfSize(width, double.PositiveInfinity));

            var visibleCount = GetFittingCount(actualTags, null, width);
            if (visibleCount >= actualTags.Count)
            {
                SetOverflowCount(overflow, 0);
                overflow.Visibility = Visibility.Collapsed;
                SetVisibleChildren(actualTags);
                return GetMeasuredSize(width);
            }

            while (visibleCount > 0)
            {
                var hiddenCount = actualTags.Count - visibleCount;
                SetOverflowCount(overflow, hiddenCount);
                overflow.Visibility = Visibility.Visible;
                overflow.Measure(new WpfSize(width, double.PositiveInfinity));

                if (Fits(actualTags.Take(visibleCount).Append(overflow), width))
                {
                    break;
                }

                visibleCount--;
            }

            SetOverflowCount(overflow, actualTags.Count - visibleCount);
            overflow.Visibility = Visibility.Visible;
            overflow.Measure(new WpfSize(width, double.PositiveInfinity));
            SetVisibleChildren(actualTags.Take(visibleCount).Append(overflow));
            return GetMeasuredSize(width);
        }

        protected override WpfSize ArrangeOverride(WpfSize finalSize)
        {
            var width = Math.Max(1, finalSize.Width);
            var x = 0d;
            var y = 0d;
            var rowHeight = 0d;

            foreach (var child in _arrangedChildren)
            {
                var desired = child.DesiredSize;
                if (x > 0 && x + desired.Width > width)
                {
                    x = 0;
                    y += rowHeight;
                    rowHeight = 0;
                }

                child.Arrange(new Rect(x, y, desired.Width, desired.Height));
                x += desired.Width;
                rowHeight = Math.Max(rowHeight, desired.Height);
            }

            foreach (UIElement child in Children)
            {
                if (!_arrangedChildren.Contains(child))
                {
                    child.Arrange(Rect.Empty);
                }
            }

            return finalSize;
        }

        private int GetFittingCount(IReadOnlyList<UIElement> children, UIElement? overflow, double width)
        {
            var count = 0;
            foreach (var child in children)
            {
                if (!Fits(children.Take(count + 1).Concat(overflow is null ? Enumerable.Empty<UIElement>() : new[] { overflow }), width))
                {
                    break;
                }

                count++;
            }

            return count;
        }

        private bool Fits(IEnumerable<UIElement> children, double width)
        {
            var x = 0d;
            var rowHeight = 0d;
            var rows = 1;
            foreach (var child in children)
            {
                var desired = child.DesiredSize;
                if (x > 0 && x + desired.Width > width)
                {
                    rows++;
                    x = 0;
                    rowHeight = 0;
                }

                if (rows > Math.Max(1, MaxRows))
                {
                    return false;
                }

                x += desired.Width;
                rowHeight = Math.Max(rowHeight, desired.Height);
            }

            return true;
        }

        private void SetVisibleChildren(IEnumerable<UIElement> children)
        {
            _arrangedChildren.Clear();
            foreach (UIElement child in Children)
            {
                var visible = children.Contains(child);
                child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible)
                {
                    _arrangedChildren.Add(child);
                }
            }
        }

        private WpfSize GetMeasuredSize(double width)
        {
            var height = 0d;
            var currentRowHeight = 0d;
            var currentX = 0d;
            foreach (var child in _arrangedChildren)
            {
                var desired = child.DesiredSize;
                if (currentX > 0 && currentX + desired.Width > width)
                {
                    height += currentRowHeight;
                    currentX = 0;
                    currentRowHeight = 0;
                }

                currentX += desired.Width;
                currentRowHeight = Math.Max(currentRowHeight, desired.Height);
            }

            return new WpfSize(width, height + currentRowHeight);
        }

        private static void SetOverflowCount(UIElement overflow, int hiddenCount)
        {
            if (GetTagModel(overflow) is { } viewModel)
            {
                viewModel.HiddenCount = Math.Max(0, hiddenCount);
            }
        }

        private static TagDisplayViewModel? GetTagModel(UIElement child) =>
            (child as FrameworkElement)?.DataContext as TagDisplayViewModel;
    }
}
