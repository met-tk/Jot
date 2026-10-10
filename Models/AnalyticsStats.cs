using System.Collections.Generic;

namespace Journal.Models
{
    public class AnalyticsStats
    {
        public int TotalWords { get; set; }
        public int TotalDays { get; set; }
        public int CurrentStreak { get; set; }
        public int AverageDailyWords { get; set; }
        public List<MonthlyWordStat> MonthlyStats { get; set; } = new();
    }

    public class MonthlyWordStat
    {
        public string MonthLabel { get; set; } = string.Empty;
        public int WordCount { get; set; }
    }
}
