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
            double availableWidth = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
            double colWidth = availableWidth / 24.0;
            double maxChildHeight = 0;

            foreach (var child in Children)
            {
                child.Measure(new Size(colWidth, double.IsInfinity(availableSize.Height) ? 1000 : availableSize.Height));
                if (child.DesiredSize.Height > maxChildHeight)
                {
                    maxChildHeight = child.DesiredSize.Height;
                }
            }

            double finalHeight = double.IsInfinity(availableSize.Height) ? (maxChildHeight > 0 ? maxChildHeight : 24) : availableSize.Height;
            return new Size(availableWidth, finalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double colWidth = finalSize.Width / 24.0;
            double colHeight = finalSize.Height;

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
