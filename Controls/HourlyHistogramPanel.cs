using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Journal.Controls
{
    /// <summary>
    /// 24 小时直方图自适应均分布局容器 (24 Columns Precise Layout)
    /// </summary>
    public class HourlyHistogramPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double availableWidth = (double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width) || availableSize.Width <= 0)
                ? 800
                : availableSize.Width;
            double colWidth = Math.Max(0, availableWidth / 24.0);
            double maxAvailHeight = (double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height) || availableSize.Height <= 0)
                ? 1000
                : availableSize.Height;
            double maxChildHeight = 0;

            foreach (var child in Children)
            {
                child.Measure(new Size(colWidth, maxAvailHeight));
                if (child.DesiredSize.Height > maxChildHeight)
                {
                    maxChildHeight = child.DesiredSize.Height;
                }
            }

            double finalHeight = (double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height) || availableSize.Height <= 0)
                ? (maxChildHeight > 0 ? maxChildHeight : 24)
                : availableSize.Height;
            return new Size(availableWidth, Math.Max(0, finalHeight));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double colWidth = Math.Max(0, finalSize.Width / 24.0);
            double colHeight = Math.Max(0, finalSize.Height);

            int index = 0;
            foreach (var child in Children)
            {
                if (index >= 24) break;
                double x = index * colWidth;
                child.Arrange(new Rect(x, 0, colWidth, colHeight));
                index++;
            }

            return finalSize;
        }
    }
}
