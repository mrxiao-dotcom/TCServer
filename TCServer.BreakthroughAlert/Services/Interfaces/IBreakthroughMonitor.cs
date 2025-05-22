using TCServer.BreakthroughAlert.Models;

namespace TCServer.BreakthroughAlert.Services.Interfaces;

public interface IBreakthroughMonitor
{
    /// <summary>
    /// 启动监控
    /// </summary>
    Task StartMonitoringAsync();

    /// <summary>
    /// 停止监控
    /// </summary>
    Task StopMonitoringAsync();

    /// <summary>
    /// 更新配置
    /// </summary>
    Task UpdateConfigAsync(BreakthroughConfig config);

    /// <summary>
    /// 获取当前状态
    /// </summary>
    MonitorStatus GetStatus();

    /// <summary>
    /// 突破事件
    /// </summary>
    event EventHandler<BreakthroughEvent> OnBreakthrough;

    /// <summary>
    /// 状态变更事件
    /// </summary>
    event EventHandler<MonitorStatus> OnStatusChanged;

    /// <summary>
    /// 错误事件
    /// </summary>
    event EventHandler<Exception> OnError;
} 