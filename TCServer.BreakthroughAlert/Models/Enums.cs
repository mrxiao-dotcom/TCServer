namespace TCServer.BreakthroughAlert.Models;

public enum BreakthroughType
{
    UpThreshold,    // 涨幅突破
    DownThreshold,  // 跌幅突破
    NewHigh,        // 新高突破
    NewLow          // 新低突破
}

public enum AlertType
{
    UpAlert,        // 涨幅提醒
    DownAlert,      // 跌幅提醒
    HighAlert,      // 新高提醒
    LowAlert        // 新低提醒
}

public enum MonitorStatus
{
    Stopped,        // 已停止
    Running,        // 运行中
    Error           // 错误
} 