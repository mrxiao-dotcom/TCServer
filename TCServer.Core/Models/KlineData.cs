using System;

namespace TCServer.Core.Models;

public class KlineData
{
    public long Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateTime OpenTime { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal Volume { get; set; }
    public DateTime CloseTime { get; set; }
    public decimal QuoteVolume { get; set; }
    public int Trades { get; set; }
    public decimal TakerBuyVolume { get; set; }
    public decimal TakerBuyQuoteVolume { get; set; }
    public DateTime CreatedAt { get; set; }
} 