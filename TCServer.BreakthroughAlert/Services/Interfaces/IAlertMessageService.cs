using TCServer.BreakthroughAlert.Models;

namespace TCServer.BreakthroughAlert.Services.Interfaces;

public interface IAlertMessageService
{
    /// <summary>
    /// 发送提醒消息
    /// </summary>
    Task<bool> SendAlertAsync(AlertMessage message);

    /// <summary>
    /// 批量发送提醒
    /// </summary>
    Task<bool> SendBatchAlertsAsync(IEnumerable<AlertMessage> messages);

    /// <summary>
    /// 更新通知配置
    /// </summary>
    Task UpdateNotificationConfigAsync(NotificationConfig config);

    /// <summary>
    /// 消息发送事件
    /// </summary>
    event EventHandler<AlertMessage> OnMessageSent;

    /// <summary>
    /// 消息发送失败事件
    /// </summary>
    event EventHandler<(AlertMessage Message, Exception Error)> OnMessageFailed;
} 