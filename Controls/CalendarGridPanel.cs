using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Journal.Controls
{
    /// <summary>
    /// 专为日历 7 列网格设计的精准布局面板。
    /// 消除流式布局在动态度量时的行重叠 Bug，保证 7 列水平等分并与星期表头（周日到周六）像素级对齐。
    /// </summary>
    public sealed class CalendarGridPanel : Panel
    {
        public static double YearZoomCellHeight { get; set; } = 24.0;

        public double RowSpacing { get; set; } = 4;
        public double ColumnSpacing { get; set; } = 4;
        public double CellHeight { get; set; } = 24;
        public bool UseYearZoomHeight { get; set; } = false;
        public bool IsAdaptiveHeight { get; set; } = false;
        public double MinAdaptiveCellHeight { get; set; } = 72;
        public bool FixSixRows { get; set; } = false;

        private void ComputeGridMetrics(Size size, out double resolvedWidth, out double colWidth, out int rowCount, out double actualCellHeight)
        {
            resolvedWidth = size.Width;
            if (double.IsInfinity(resolvedWidth) || double.IsNaN(resolvedWidth) || resolvedWidth <= 0)
            {
                resolvedWidth = 200;
            }
            colWidth = Math.Max(0, (resolvedWidth - ColumnSpacing * 6) / 7.0);

            int count = Children.Count;
            rowCount = FixSixRows ? 6 : (count + 6) / 7;
            if (rowCount == 0) rowCount = 6;

            actualCellHeight = UseYearZoomHeight ? YearZoomCellHeight : CellHeight;
            if (IsAdaptiveHeight)
            {
                if (double.IsInfinity(size.Height) || double.IsNaN(size.Height) || size.Height <= 0)
                {
                    actualCellHeight = Math.Max(MinAdaptiveCellHeight, CellHeight);
                }
                else
                {
                    double remainHeight = size.Height - Math.Max(0, rowCount - 1) * RowSpacing;
                    actualCellHeight = Math.Max(MinAdaptiveCellHeight, remainHeight / (double)rowCount);
                }
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            ComputeGridMetrics(availableSize, out double availableWidth, out double colWidth, out int rowCount, out double actualCellHeight);

            var childAvailableSize = new Size(colWidth, Math.Max(0, actualCellHeight));

            foreach (var child in Children)
            {
                child.Measure(childAvailableSize);
            }

            double totalHeight = rowCount * actualCellHeight + Math.Max(0, rowCount - 1) * RowSpacing;
            return new Size(availableWidth, totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            ComputeGridMetrics(finalSize, out _, out double colWidth, out int rowCount, out double actualCellHeight);

            for (int i = 0; i < Children.Count; i++)
            {
                int row = i / 7;
                int col = i % 7;

                double x = col * (colWidth + ColumnSpacing);
                double y = row * (actualCellHeight + RowSpacing);

                Children[i].Arrange(new Rect(x, y, colWidth, actualCellHeight));
            }

            double totalHeight = rowCount * actualCellHeight + Math.Max(0, rowCount - 1) * RowSpacing;
            return new Size(finalSize.Width, totalHeight);
        }
    }
}
