using System;

namespace TCServer.Common.Models
{
    /// <summary>
    /// 实时tick数据模型
    /// </summary>
    public class TickData
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal LastPrice { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal Volume { get; set; }
        public decimal QuoteVolume { get; set; }
        public decimal PriceChangePercent { get; set; }  // 24小时涨跌幅
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
} 