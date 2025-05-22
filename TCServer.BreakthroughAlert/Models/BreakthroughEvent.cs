namespace TCServer.BreakthroughAlert.Models;

public class BreakthroughEvent
{
    public string Symbol { get; set; } = string.Empty;
    public decimal CurrentPrice { get; set; }
    public decimal ThresholdValue { get; set; }
    public BreakthroughType Type { get; set; }
    public DateTime EventTime { get; set; } = DateTime.Now;
    public decimal ChangePercent { get; set; }
    public decimal Volume { get; set; }
    public string Description { get; set; } = string.Empty;

    public decimal Percentage => ChangePercent;
    public bool IsUptrend => Type == BreakthroughType.UpThreshold || Type == BreakthroughType.NewHigh;
    public DateTime Timestamp => EventTime;
    public bool IsHighLowBreakthrough => Type == BreakthroughType.NewHigh || Type == BreakthroughType.NewLow;
    public int Days { get; set; }
    public bool IsHigh => Type == BreakthroughType.NewHigh;
}

public class AlertMessage
{
    public string Symbol { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public AlertType Type { get; set; }
    public DateTime AlertTime { get; set; } = DateTime.Now;
    public decimal CurrentPrice { get; set; }
    public decimal ChangePercent { get; set; }
    public decimal Volume { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class AlertLog
{
    public string Symbol { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public AlertType Type { get; set; }
    public DateTime AlertTime { get; set; } = DateTime.Now;
    public decimal CurrentPrice { get; set; }
    public decimal ChangePercent { get; set; }
    public decimal Volume { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsSent { get; set; }
    public string? ErrorMessage { get; set; }
} 