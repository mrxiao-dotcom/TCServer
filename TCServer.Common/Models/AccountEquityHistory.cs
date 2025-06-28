using System;

namespace TCServer.Common.Models
{
    /// <summary>
    /// 账户权益历史模型
    /// </summary>
    public class AccountEquityHistory
    {
        public long Id { get; set; }
        public string AccountId { get; set; } = string.Empty;
        public decimal Equity { get; set; }
        public decimal Available { get; set; }
        public decimal PositionValue { get; set; }
        public decimal Leverage { get; set; }
        public decimal LongValue { get; set; }
        public decimal ShortValue { get; set; }
        public int LongCount { get; set; }
        public int ShortCount { get; set; }
        public DateTime CreateTime { get; set; }
    }
} 