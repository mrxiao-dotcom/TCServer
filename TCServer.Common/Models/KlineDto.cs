using System;

namespace TCServer.Common.Models
{
    /// <summary>
    /// 币安API返回的K线数据传输对象
    /// </summary>
    public class KlineDto
    {
        public DateTime OpenTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public DateTime CloseTime { get; set; }
        public decimal QuoteVolume { get; set; }
        public int TradeCount { get; set; }
        public decimal TakerBuyVolume { get; set; }
        public decimal TakerBuyQuoteVolume { get; set; }
    }
} 