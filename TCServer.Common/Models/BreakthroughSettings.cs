using System.Collections.Generic;

namespace TCServer.Common.Models
{
    public class BreakthroughSettings
    {
        public bool EnableNotifications { get; set; } = true;
        public bool EnableHighLowBreakthrough { get; set; } = true;
        
        public decimal Threshold1 { get; set; } = 5.0m;
        public decimal Threshold2 { get; set; } = 10.0m;
        public decimal Threshold3 { get; set; } = 20.0m;
        
        public bool Threshold1Enabled { get; set; } = true;
        public bool Threshold2Enabled { get; set; } = true;
        public bool Threshold3Enabled { get; set; } = true;
        
        public int HighLowDays1 { get; set; } = 5;
        public int HighLowDays2 { get; set; } = 10;
        public int HighLowDays3 { get; set; } = 20;
        
        public bool HighLowDays1Enabled { get; set; } = true;
        public bool HighLowDays2Enabled { get; set; } = true;
        public bool HighLowDays3Enabled { get; set; } = true;
        
        public List<string> Tokens { get; set; } = new List<string>();
        public string NotificationUrl { get; set; } = string.Empty;
        
        public Dictionary<string, Dictionary<int, (bool Exceeded, decimal LastPercentage)>> LastExceededState { get; set; } = new();
        public Dictionary<string, Dictionary<int, (bool ExceededHigh, bool ExceededLow, decimal LastHigh, decimal LastLow)>> LastHighLowState { get; set; } = new();
    }
} 