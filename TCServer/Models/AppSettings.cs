using System;
using System.Collections.Generic;

namespace TCServer.Models
{
    public class AppSettings
    {
        public BreakthroughSettings BreakthroughSettings { get; set; } = new BreakthroughSettings();
    }

    public class BreakthroughSettings
    {
        public List<string> Tokens { get; set; } = new List<string>();
        public bool EnableNotifications { get; set; }
        public decimal Threshold1 { get; set; } = 5.0m;
        public decimal Threshold2 { get; set; } = 10.0m;
        public decimal Threshold3 { get; set; } = 20.0m;
        public bool Threshold1Enabled { get; set; }
        public bool Threshold2Enabled { get; set; }
        public bool Threshold3Enabled { get; set; }
        public Dictionary<string, Dictionary<int, (bool Exceeded, decimal LastPercentage)>> LastExceededState { get; set; } = new();

        // 新高/新低突破设置
        public bool EnableHighLowBreakthrough { get; set; }
        public int HighLowDays1 { get; set; } = 5;
        public int HighLowDays2 { get; set; } = 10;
        public int HighLowDays3 { get; set; } = 20;
        public bool HighLowDays1Enabled { get; set; }
        public bool HighLowDays2Enabled { get; set; }
        public bool HighLowDays3Enabled { get; set; }
        public Dictionary<string, Dictionary<int, (bool ExceededHigh, bool ExceededLow, decimal LastHigh, decimal LastLow)>> LastHighLowState { get; set; } = new();
    }
} 