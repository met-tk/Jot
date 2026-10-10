using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Journal.Controls
{
    /// <summary>
    /// 自由螺旋词云排版面板：基于黄金分割角与扁椭圆阿基米德螺旋线碰撞检测算法，
    /// 让高频核心大词居于视觉中心，中低频词自由穿插嵌入四周空隙，彻底告别机械的“按行对齐”
    /// </summary>
    public class WordCloudPanel : Panel
    {
        public static readonly DependencyProperty HorizontalSpacingProperty =
            DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WordCloudPanel),
                new PropertyMetadata(6.0, OnSpacingChanged));

        public static readonly DependencyProperty VerticalSpacingProperty =
            DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WordCloudPanel),
                new PropertyMetadata(4.0, OnSpacingChanged));

        public double HorizontalSpacing
        {
            get => (double)GetValue(HorizontalSpacingProperty);
            set => SetValue(HorizontalSpacingProperty, value);
        }

        public double VerticalSpacing
        {
            get => (double)GetValue(VerticalSpacingProperty);
            set => SetValue(VerticalSpacingProperty, value);
        }

        private SpiralLayoutResult? _cachedLayout;
        private double _cachedMaxWidth = -1;

        private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is WordCloudPanel panel)
            {
                panel._cachedLayout = null;
                panel.InvalidateMeasure();
                panel.InvalidateArrange();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in Children)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }

            double maxWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
                ? 680.0
                : availableSize.Width;

            var layout = ComputeSpiralLayout(maxWidth);
            _cachedLayout = layout;
            _cachedMaxWidth = maxWidth;

            if (layout.Placements.Count == 0)
            {
                return new Size(0, 0);
            }

            double finalWidth = double.IsInfinity(availableSize.Width)
                ? layout.CloudWidth
                : Math.Min(availableSize.Width, Math.Max(layout.CloudWidth, 0));

            return new Size(Math.Max(0, finalWidth), Math.Max(0, layout.CloudHeight));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double maxWidth = finalSize.Width > 0 ? finalSize.Width : 680.0;
            var layout = (_cachedLayout.HasValue && Math.Abs(_cachedMaxWidth - maxWidth) < 0.5 && _cachedLayout.Value.Placements.Count == CountVisibleChildren())
                ? _cachedLayout.Value
                : ComputeSpiralLayout(maxWidth);

            if (layout.Placements.Count == 0)
            {
                return finalSize;
            }

            // 将螺旋外接矩形整体平移至正坐标，并在容器宽度内水平居中
            double offsetX = -layout.MinX + Math.Max(0, (finalSize.Width - layout.CloudWidth) / 2.0);
            double offsetY = -layout.MinY;

            foreach (var (element, rect) in layout.Placements)
            {
                var finalRect = new Rect(
                    rect.X + offsetX,
                    rect.Y + offsetY,
                    rect.Width,
                    rect.Height);
                element.Arrange(finalRect);
            }

            return new Size(finalSize.Width, Math.Max(finalSize.Height, layout.CloudHeight));
        }

        private int CountVisibleChildren()
        {
            int count = 0;
            foreach (UIElement child in Children)
            {
                if (child.Visibility != Visibility.Collapsed)
                {
                    count++;
                }
            }
            return count;
        }

        private readonly struct SpiralLayoutResult
        {
            public List<(UIElement Element, Rect Rect)> Placements { get; init; }
            public double MinX { get; init; }
            public double MinY { get; init; }
            public double CloudWidth { get; init; }
            public double CloudHeight { get; init; }
        }

        private SpiralLayoutResult ComputeSpiralLayout(double maxWidth)
        {
            var visibleChildren = new List<(UIElement Element, Size Desired, int Index)>();
            int idx = 0;
            foreach (UIElement child in Children)
            {
                if (child.Visibility != Visibility.Collapsed)
                {
                    visibleChildren.Add((child, child.DesiredSize, idx));
                }
                idx++;
            }

            if (visibleChildren.Count == 0)
            {
                return new SpiralLayoutResult
                {
                    Placements = new List<(UIElement, Rect)>(),
                    MinX = 0,
                    MinY = 0,
                    CloudWidth = 0,
                    CloudHeight = 0
                };
            }

            // 按视觉体量（高度优先，兼顾面积）从大到小排序，确保最大核心词锚定中心，小词自由填缝
            visibleChildren.Sort((a, b) =>
            {
                int cmp = b.Desired.Height.CompareTo(a.Desired.Height);
                if (cmp != 0) return cmp;
                cmp = b.Desired.Width.CompareTo(a.Desired.Width);
                return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
            });

            double hGap = Math.Max(1.0, HorizontalSpacing);
            double vGap = Math.Max(1.0, VerticalSpacing);

            // 扁椭圆长短轴比例：横向自然舒展，纵向紧凑收敛
            const double aspectX = 1.72;
            const double aspectY = 0.62;
            const double goldenAngle = 2.39996322972865332; // 137.50776° 黄金分割角

            var placements = new List<(UIElement Element, Rect Rect)>(visibleChildren.Count);
            var placedRects = new List<Rect>(visibleChildren.Count);

            double minX = 0, maxX = 0, minY = 0, maxY = 0;

            for (int i = 0; i < visibleChildren.Count; i++)
            {
                var (elem, desired, _) = visibleChildren[i];
                double w = Math.Max(1.0, desired.Width);
                double h = Math.Max(1.0, desired.Height);

                if (i == 0)
                {
                    // 第 0 号最大核心词锚定在螺旋中心偏左上微调位，营造视觉主焦点
                    var firstRect = new Rect(-w / 2.0, -h / 2.0, w, h);
                    placedRects.Add(firstRect);
                    placements.Add((elem, firstRect));
                    minX = firstRect.Left;
                    maxX = firstRect.Right;
                    minY = firstRect.Top;
                    maxY = firstRect.Bottom;
                    continue;
                }

                // 每个词以黄金分割角递增作为初始发射角，配合微小的奇偶高低错位，彻底打破水平行感
                double startTheta = i * goldenAngle;
                double verticalJitter = ((i % 3) - 1) * 3.5;

                Rect chosenRect = default;
                bool found = false;

                for (int step = 0; step < 1600; step++)
                {
                    double theta = startTheta + step * 0.32;
                    double radius = 2.0 + step * 0.95;

                    double cx = radius * Math.Cos(theta) * aspectX;
                    double cy = radius * Math.Sin(theta) * aspectY + verticalJitter;

                    var candidate = new Rect(cx - w / 2.0, cy - h / 2.0, w, h);

                    // 限制横向总跨度不超过容器可用宽度（留出安全容差，仅当步数很大时放宽兜底）
                    double newMinX = Math.Min(minX, candidate.Left);
                    double newMaxX = Math.Max(maxX, candidate.Right);
                    if (step < 1100 && (newMaxX - newMinX) > maxWidth && w < maxWidth)
                    {
                        continue;
                    }

                    // 2D 包围盒碰撞检测（带间距缓冲）
                    bool collides = false;
                    for (int j = 0; j < placedRects.Count; j++)
                    {
                        var existing = placedRects[j];
                        if (candidate.Left < existing.Right + hGap &&
                            candidate.Right + hGap > existing.Left &&
                            candidate.Top < existing.Bottom + vGap &&
                            candidate.Bottom + vGap > existing.Top)
                        {
                            collides = true;
                            break;
                        }
                    }

                    if (!collides)
                    {
                        chosenRect = candidate;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // 极端窄屏兜底：紧贴当前底部居中放置
                    chosenRect = new Rect(-w / 2.0, maxY + vGap, w, h);
                }

                placedRects.Add(chosenRect);
                placements.Add((elem, chosenRect));

                if (chosenRect.Left < minX) minX = chosenRect.Left;
                if (chosenRect.Right > maxX) maxX = chosenRect.Right;
                if (chosenRect.Top < minY) minY = chosenRect.Top;
                if (chosenRect.Bottom > maxY) maxY = chosenRect.Bottom;
            }

            return new SpiralLayoutResult
            {
                Placements = placements,
                MinX = minX,
                MinY = minY,
                CloudWidth = Math.Max(0, maxX - minX),
                CloudHeight = Math.Max(0, maxY - minY)
            };
        }
    }
}
