using System.Net.Http.Json;
using Serilog;
using TCServer.BreakthroughAlert.Models;
using TCServer.BreakthroughAlert.Services.Interfaces;
using Newtonsoft.Json;

namespace TCServer.BreakthroughAlert.Services;

public class AlertMessageService : IAlertMessageService
{
    private readonly IFileStorageService _storageService;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private NotificationConfig _notificationConfig;
    private readonly object _lockObj = new();

    public event EventHandler<AlertMessage>? OnMessageSent;
    public event EventHandler<(AlertMessage Message, Exception Error)>? OnMessageFailed;

    public AlertMessageService(
        IFileStorageService storageService,
        ILogger logger,
        HttpClient httpClient)
    {
        _storageService = storageService;
        _logger = logger;
        _httpClient = httpClient;
        _notificationConfig = new NotificationConfig();
    }

    public async Task<bool> SendAlertAsync(AlertMessage message)
    {
        try
        {
            if (string.IsNullOrEmpty(_notificationConfig.NotificationUrl))
            {
                _logger.Warning("通知URL未配置，消息未发送");
                return false;
            }

            var content = FormatMessage(message);
            var response = await _httpClient.PostAsJsonAsync(_notificationConfig.NotificationUrl, new
            {
                content = content,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });

            if (response.IsSuccessStatusCode)
            {
                _logger.Information($"消息发送成功: {message.Symbol} {message.Type}");
                OnMessageSent?.Invoke(this, message);
                var alertLog = new AlertLog
                {
                    Symbol = message.Symbol,
                    Message = content,
                    Type = message.Type,
                    AlertTime = message.AlertTime,
                    CurrentPrice = message.CurrentPrice,
                    ChangePercent = message.ChangePercent,
                    Volume = message.Volume,
                    Description = message.Description,
                    IsSent = true
                };
                var logContent = JsonConvert.SerializeObject(alertLog);
                await _storageService.AppendLogAsync(logContent);
                return true;
            }
            else
            {
                var error = $"发送失败: {response.StatusCode}";
                _logger.Error(error);
                OnMessageFailed?.Invoke(this, (message, new Exception(error)));
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"发送消息失败: {message.Symbol} {message.Type}");
            OnMessageFailed?.Invoke(this, (message, ex));
            return false;
        }
    }

    public async Task<bool> SendBatchAlertsAsync(IEnumerable<AlertMessage> messages)
    {
        var success = true;
        var count = 0;
        foreach (var message in messages)
        {
            if (await SendAlertAsync(message))
            {
                count++;
            }
            else
            {
                success = false;
            }
        }
        if (count > 0)
        {
            _logger.Information($"推送成功：共 {count} 条消息");
        }
        return success;
    }

    public async Task UpdateNotificationConfigAsync(NotificationConfig config)
    {
        lock (_lockObj)
        {
            _notificationConfig = config;
        }
        _logger.Information("更新通知配置成功");
    }

    private string FormatMessage(AlertMessage message)
    {
        var template = _notificationConfig.MessageTemplate;
        return template
            .Replace("{symbol}", message.Symbol)
            .Replace("{type}", GetAlertTypeText(message.Type))
            .Replace("{price}", message.CurrentPrice.ToString("F8"))
            .Replace("{change}", message.ChangePercent.ToString("F2"))
            .Replace("{volume}", FormatVolume(message.Volume))
            .Replace("{time}", message.AlertTime.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private string GetAlertTypeText(AlertType type)
    {
        return type switch
        {
            AlertType.UpAlert => "涨幅",
            AlertType.DownAlert => "跌幅",
            AlertType.HighAlert => "新高",
            AlertType.LowAlert => "新低",
            _ => "未知"
        };
    }

    private string FormatVolume(decimal volume)
    {
        if (volume >= 1000000)
            return $"{volume / 1000000:F2}M";
        if (volume >= 1000)
            return $"{volume / 1000:F2}K";
        return volume.ToString("F2");
    }
} 