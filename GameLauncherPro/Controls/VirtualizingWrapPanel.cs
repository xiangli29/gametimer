using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GameLauncherPro.Controls
{
    // Simple VirtualizingWrapPanel: assumes fixed ItemWidth and ItemHeight
    // Provides basic vertical virtualization and implements IScrollInfo
    public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
    {
        public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
            nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
            nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double ItemWidth
        {
            get => (double)GetValue(ItemWidthProperty);
            set => SetValue(ItemWidthProperty, value);
        }

        public double ItemHeight
        {
            get => (double)GetValue(ItemHeightProperty);
            set => SetValue(ItemHeightProperty, value);
        }

        private IItemContainerGenerator _generator = null!;

        public VirtualizingWrapPanel()
        {
            this._generator = this.ItemContainerGenerator;
        }

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        {
            UpdateScrollInfo(availableSize);

            if (this._generator == null)
                this._generator = this.ItemContainerGenerator;

            var itemsControl = ItemsControl.GetItemsOwner(this);
            int itemCount = itemsControl?.Items.Count ?? 0;

            if (itemCount == 0)
            {
                // 返回有限大小，避免将 Infinity 传播为 DesiredSize
                double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
                double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
                return new System.Windows.Size(w, h);
            }

            double itemW = ItemWidth;
            double itemH = ItemHeight;
            if (itemW <= 0 || itemH <= 0)
            {
                // return a finite default size
                return new System.Windows.Size(0, 0);
            }

            // Handle infinite available size (e.g., when placed in a StackPanel). Use sensible defaults.
            double availableWidth = double.IsInfinity(availableSize.Width) ? itemW : availableSize.Width;
            double availableHeight = double.IsInfinity(availableSize.Height) ? itemH : availableSize.Height;

            int itemsPerRow = Math.Max(1, (int)Math.Floor(availableWidth / itemW));
            int rowCount = (int)Math.Ceiling((double)itemCount / itemsPerRow);

            int visibleRows;
            if (double.IsInfinity(availableSize.Height)) visibleRows = rowCount; else visibleRows = (int)Math.Ceiling(availableHeight / itemH) + 1; // one extra row buffer

            int firstVisibleRow = (int)Math.Floor(VerticalOffset / itemH);
            int firstIndex = firstVisibleRow * itemsPerRow;
            int lastIndex = Math.Min(itemCount - 1, (firstVisibleRow + visibleRows) * itemsPerRow - 1);

            CleanUpItems(firstIndex, lastIndex);

            var startPos = _generator.GeneratorPositionFromIndex(firstIndex);
            int childIndex = (startPos.Offset == 0) ? startPos.Index : startPos.Index + 1;

            using (_generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
                {
                    bool newlyRealized = false;
                    var child = (UIElement)_generator.GenerateNext(out newlyRealized);
                    if (newlyRealized)
                    {
                        if (childIndex >= this.Children.Count)
                            base.AddInternalChild(child);
                        else
                            base.InsertInternalChild(childIndex, child);
                        _generator.PrepareItemContainer(child);
                    }
                    else
                    {
                        // already realized
                    }

                    child.Measure(new System.Windows.Size(itemW, itemH));
                }
            }

            // Compute extent and desired size (must be finite)
            ExtentWidth = itemsPerRow * itemW;
            ExtentHeight = rowCount * itemH;

            double desiredWidth = double.IsInfinity(availableSize.Width) ? ExtentWidth : availableSize.Width;
            double desiredHeight = double.IsInfinity(availableSize.Height) ? ExtentHeight : availableSize.Height;

            return new System.Windows.Size(desiredWidth, desiredHeight);
        }

        protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
        {
            double itemW = ItemWidth;
            double itemH = ItemHeight;
            if (itemW <= 0 || itemH <= 0) return finalSize;

            int itemsPerRow = Math.Max(1, (int)Math.Floor(finalSize.Width / itemW));

            int childCount = this.Children.Count;
            if (this._generator == null)
                this._generator = this.ItemContainerGenerator;

            int startIndex;
            try
            {
                startIndex = _generator.IndexFromGeneratorPosition(new GeneratorPosition(0, 0));
            }
            catch
            {
                startIndex = 0;
            }
            if (startIndex < 0) startIndex = 0;

            for (int i = 0; i < childCount; i++)
            {
                var child = this.Children[i];
                if (child == null) continue;
                int itemIndex = startIndex + i;
                int row = itemIndex / itemsPerRow;
                int col = itemIndex % itemsPerRow;
                double x = col * itemW - HorizontalOffset;
                double y = row * itemH - VerticalOffset;
                child.Arrange(new Rect(x, y, itemW, itemH));
            }

            return finalSize;
        }

        private void CleanUpItems(int firstIndex, int lastIndex)
        {
            for (int i = this.Children.Count - 1; i >= 0; i--)
            {
                var genPos = new GeneratorPosition(i, 0);
                int itemIndex = _generator.IndexFromGeneratorPosition(genPos);
                if (itemIndex < firstIndex || itemIndex > lastIndex)
                {
                    _generator.Remove(genPos, 1);
                    RemoveInternalChildRange(i, 1);
                }
            }
        }

        #region IScrollInfo implementation (basic)
        private ScrollViewer? _owner;
        public bool CanHorizontallyScroll { get; set; }
        public bool CanVerticallyScroll { get; set; } = true;
        public double ExtentHeight { get; private set; }
        public double ExtentWidth { get; private set; }
        public double ViewportHeight { get; private set; }
        public double ViewportWidth { get; private set; }
        public double HorizontalOffset { get; private set; }
        public double VerticalOffset { get; private set; }

        public ScrollViewer? ScrollOwner { get => _owner; set => _owner = value; }

        private void UpdateScrollInfo(System.Windows.Size availableSize)
        {
            var itemsControl = ItemsControl.GetItemsOwner(this);
            int itemCount = itemsControl?.Items.Count ?? 0;
            double itemW = ItemWidth;
            double itemH = ItemHeight;
            if (itemW <= 0 || itemH <= 0) return;

            int itemsPerRow = Math.Max(1, (int)Math.Floor(availableSize.Width / itemW));
            int rowCount = (int)Math.Ceiling((double)itemCount / itemsPerRow);

            ExtentHeight = rowCount * itemH;
            ExtentWidth = availableSize.Width;
            ViewportHeight = availableSize.Height;
            ViewportWidth = availableSize.Width;

            if (ScrollOwner != null)
                ScrollOwner.InvalidateScrollInfo();
        }

        public void LineDown() => SetVerticalOffset(VerticalOffset + 20);
        public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 20);
        public void LineRight() => SetHorizontalOffset(HorizontalOffset + 20);
        public void LineUp() => SetVerticalOffset(VerticalOffset - 20);
        public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
        public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + SystemParameters.WheelScrollLines * ItemHeight);
        public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - SystemParameters.WheelScrollLines * ItemWidth);
        public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + SystemParameters.WheelScrollLines * ItemWidth);
        public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - SystemParameters.WheelScrollLines * ItemHeight);
        public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
        public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
        public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);
        public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);

        public void SetHorizontalOffset(double offset)
        {
            if (offset < 0) offset = 0;
            HorizontalOffset = offset;
            InvalidateMeasure();
        }

        public void SetVerticalOffset(double offset)
        {
            if (offset < 0) offset = 0;
            if (offset + ViewportHeight > ExtentHeight) offset = Math.Max(0, ExtentHeight - ViewportHeight);
            VerticalOffset = offset;
            InvalidateMeasure();
        }

        #endregion
    }
}
