using System.Text.Json.Serialization;

namespace TCServer.BreakthroughAlert.Models;

public class BreakthroughConfig
{
    public string Version { get; set; } = "1.0";
    public DateTime LastModified { get; set; } = DateTime.Now;
    
    // 涨幅阈值配置
    public List<ThresholdConfig> UpThresholds { get; set; } = new()
    {
        new ThresholdConfig { Value = 5.0m, IsEnabled = true, Description = "5%涨幅" },
        new ThresholdConfig { Value = 10.0m, IsEnabled = true, Description = "10%涨幅" },
        new ThresholdConfig { Value = 15.0m, IsEnabled = true, Description = "15%涨幅" }
    };
    
    // 跌幅阈值配置
    public List<ThresholdConfig> DownThresholds { get; set; } = new()
    {
        new ThresholdConfig { Value = -5.0m, IsEnabled = true, Description = "5%跌幅" },
        new ThresholdConfig { Value = -10.0m, IsEnabled = true, Description = "10%跌幅" },
        new ThresholdConfig { Value = -15.0m, IsEnabled = true, Description = "15%跌幅" }
    };
    
    // 新高监控配置
    public List<NewHighConfig> NewHighConfigs { get; set; } = new()
    {
        new NewHighConfig { Days = 5, IsEnabled = true, Description = "5天新高" },
        new NewHighConfig { Days = 10, IsEnabled = true, Description = "10天新高" },
        new NewHighConfig { Days = 20, IsEnabled = true, Description = "20天新高" }
    };
    
    // 新低监控配置
    public List<NewLowConfig> NewLowConfigs { get; set; } = new()
    {
        new NewLowConfig { Days = 5, IsEnabled = true, Description = "5天新低" },
        new NewLowConfig { Days = 10, IsEnabled = true, Description = "10天新低" },
        new NewLowConfig { Days = 20, IsEnabled = true, Description = "20天新低" }
    };
    
    // 通知配置
    public NotificationConfig NotificationConfig { get; set; } = new()
    {
        NotificationUrl = "",
        MessageTemplate = "{symbol} {type}突破提醒：当前价格{price}，变化幅度{change}%",
        RetryCount = 3,
        RetryIntervalSeconds = 5
    };
    
    // 汇总时间窗口（秒）
    public int SummaryWindowSeconds { get; set; } = 60;
}

public class ThresholdConfig
{
    public decimal Value { get; set; }
    public bool IsEnabled { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class NewHighConfig
{
    public int Days { get; set; }
    public bool IsEnabled { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class NewLowConfig
{
    public int Days { get; set; }
    public bool IsEnabled { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class NotificationConfig
{
    public string NotificationUrl { get; set; } = string.Empty;
    public string MessageTemplate { get; set; } = string.Empty;
    public int RetryCount { get; set; } = 3;
    public int RetryIntervalSeconds { get; set; } = 5;
} 