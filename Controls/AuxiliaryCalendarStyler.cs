using System;
using System.Collections.Generic;
using Journal.Common;
using Journal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Journal.Controls
{
    /// <summary>
    /// 统一管理辅助 CalendarView 的已实例化日期容器追踪、O(1) 虚拟化回收与打卡高亮染色
    /// </summary>
    public sealed class AuxiliaryCalendarStyler
    {
        private readonly Dictionary<DateTime, CalendarViewDayItem> _realizedDayItems = new();
        private readonly Dictionary<CalendarViewDayItem, DateTime> _itemToDate = new();

        public void Clear()
        {
            _realizedDayItems.Clear();
            _itemToDate.Clear();
        }

        public void HandleDayItemChanging(CalendarViewDayItemChangingEventArgs args, IReadOnlySet<DateTime> activeDates)
        {
            var item = args?.Item;
            if (item == null) return;

            if (args!.InRecycleQueue)
            {
                if (_itemToDate.TryGetValue(item, out var oldDate))
                {
                    _itemToDate.Remove(item);
                    if (_realizedDayItems.TryGetValue(oldDate, out var existing) && ReferenceEquals(existing, item))
                    {
                        _realizedDayItems.Remove(oldDate);
                    }
                }
                return;
            }

            if (args.Phase == 0)
            {
                if (_itemToDate.TryGetValue(item, out var previousDate))
                {
                    _itemToDate.Remove(item);
                    if (_realizedDayItems.TryGetValue(previousDate, out var existing) && ReferenceEquals(existing, item))
                    {
                        _realizedDayItems.Remove(previousDate);
                    }
                }

                var itemDate = item.Date.Date;
                _realizedDayItems[itemDate] = item;
                _itemToDate[item] = itemDate;
                ApplyDayItemStyle(item, activeDates != null && activeDates.Contains(itemDate));
            }
        }

        public void Refresh(IReadOnlySet<DateTime> activeDates)
        {
            if (_realizedDayItems.Count == 0) return;

            foreach (var kvp in _realizedDayItems)
            {
                if (kvp.Value != null)
                {
                    ApplyDayItemStyle(kvp.Value, activeDates != null && activeDates.Contains(kvp.Key));
                }
            }
        }

        public static void ApplyDayItemStyle(CalendarViewDayItem item, bool hasDiary)
        {
            try
            {
                item.SetDensityColors(null);
                if (hasDiary)
                {
                    item.Background = AmbianceThemeEngine.ActiveCalendarDayBackgroundBrush;
                    item.BorderBrush = AmbianceThemeEngine.ActiveCalendarDayRingBrush;
                    item.BorderThickness = new Thickness(1.5);
                    item.CornerRadius = new CornerRadius(20);
                }
                else
                {
                    item.ClearValue(Control.BackgroundProperty);
                    item.ClearValue(Control.BorderBrushProperty);
                    item.ClearValue(Control.BorderThicknessProperty);
                }
            }
            catch { }
        }
    }
}
