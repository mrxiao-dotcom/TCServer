using System;

namespace TCServer.Common.Models
{
    /// <summary>
    /// 账户持仓模型
    /// </summary>
    public class AccountPosition
    {
        public long Id { get; set; }
        public int AccountId { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string PositionSide { get; set; } = string.Empty; // LONG, SHORT
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal PositionAmt { get; set; }
        public int Leverage { get; set; }
        public string MarginType { get; set; } = string.Empty; // ISOLATED, CROSS
        public decimal? IsolatedMargin { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public DateTime RecordDate { get; set; }
        public decimal? LiquidationPrice { get; set; }
        public DateTime Timestamp { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
} 