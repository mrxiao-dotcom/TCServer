using System;

namespace TCServer.Common.Models
{
    /// <summary>
    /// 账户余额模型
    /// </summary>
    public class AccountBalance
    {
        public long Id { get; set; }
        public int AccountId { get; set; }
        public decimal TotalEquity { get; set; }
        public decimal AvailableBalance { get; set; }
        public decimal MarginBalance { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public DateTime Timestamp { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        
        /// <summary>
        /// 账户名称（用于显示）
        /// </summary>
        public string? AccountName { get; set; }
    }
} 