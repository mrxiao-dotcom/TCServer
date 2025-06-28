using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace TCServer.Core.Services
{
    /// <summary>
    /// 虾推啥通知推送服务
    /// </summary>
    public class NotificationService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<NotificationService> _logger;
        private bool _disposed = false;

        public NotificationService(ILogger<NotificationService> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "TCServer-Notification/1.0");
        }

        /// <summary>
        /// 发送虾推啥通知
        /// </summary>
        /// <param name="token">虾推啥token</param>
        /// <param name="title">消息标题</param>
        /// <param name="content">消息内容</param>
        /// <returns>是否发送成功</returns>
        public async Task<bool> SendXtuisNotificationAsync(string token, string title, string content)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("虾推啥token为空，跳过推送");
                return false;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                _logger.LogWarning("推送标题为空，跳过推送");
                return false;
            }

            try
            {
                _logger.LogInformation($"📱 开始发送虾推啥通知: {title}");

                // URL编码处理
                var encodedTitle = Uri.EscapeDataString(title);
                var encodedContent = Uri.EscapeDataString(content ?? "");

                // 构建请求URL
                var url = $"https://wx.xtuis.cn/{token}.send?text={encodedTitle}&desp={encodedContent}";
                
                _logger.LogDebug($"推送URL: {url}");

                // 发送GET请求
                var response = await _httpClient.GetAsync(url);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"✅ 虾推啥通知发送成功: {title}");
                    _logger.LogDebug($"响应内容: {responseContent}");
                    return true;
                }
                else
                {
                    _logger.LogError($"❌ 虾推啥通知发送失败: HTTP {(int)response.StatusCode}, 响应: {responseContent}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ 发送虾推啥通知时发生异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 测试虾推啥token是否有效
        /// </summary>
        /// <param name="token">虾推啥token</param>
        /// <returns>是否有效</returns>
        public async Task<bool> TestXtuisTokenAsync(string token)
        {
            return await SendXtuisNotificationAsync(token, "TCServer测试消息", "这是一条测试消息，如果收到说明配置正确！");
        }

        /// <summary>
        /// 格式化账户权益推送消息
        /// </summary>
        /// <param name="accountBalances">账户余额列表</param>
        /// <returns>格式化的消息内容</returns>
        public string FormatAccountBalancesMessage(List<(string AccountName, decimal TotalEquity, decimal UnrealizedPnl, DateTime UpdateTime)> accountBalances)
        {
            if (accountBalances == null || accountBalances.Count == 0)
            {
                return "当前没有账户数据";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"📊 TCServer账户监管报告");
            sb.AppendLine($"🕐 {DateTime.Now:MM-dd HH:mm}");
            sb.AppendLine();

            // 表格标题
            sb.AppendLine("```");
            sb.AppendLine("账户名     │  权益   │  浮盈  ");
            sb.AppendLine("─────────────────────────────");

            decimal totalEquity = 0;
            decimal totalPnl = 0;

            foreach (var (accountName, equity, pnl, updateTime) in accountBalances)
            {
                totalEquity += equity;
                totalPnl += pnl;

                var pnlIcon = pnl >= 0 ? "+" : "";
                var pnlText = $"{pnlIcon}{pnl:F0}";
                
                // 限制账户名长度，确保对齐
                var displayName = accountName.Length > 8 ? accountName.Substring(0, 8) : accountName;
                
                sb.AppendLine($"{displayName,-10}│{equity,7:F0}│{pnlText,7}");
            }

            sb.AppendLine("─────────────────────────────");
            
            // 汇总行
            var totalPnlIcon = totalPnl >= 0 ? "+" : "";
            var totalPnlText = $"{totalPnlIcon}{totalPnl:F0}";
            sb.AppendLine($"{"合计",-10}│{totalEquity,7:F0}│{totalPnlText,7}");
            sb.AppendLine("```");

            return sb.ToString();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
} 