using Microsoft.Extensions.Logging;
using TCServer.Common.Interfaces;
using TCServer.Common.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TCServer.Core.Services
{
    /// <summary>
    /// 账户数据查询服务
    /// </summary>
    public class AccountDataService : IDisposable
    {
        private readonly BinanceApiService _binanceApiService;
        private readonly IAccountRepository _accountRepository;
        private readonly ILogger<AccountDataService> _logger;
        private readonly NotificationService _notificationService;
        private Timer? _timer;
        private Timer? _pushTimer;
        private bool _isRunning = false;
        private bool _disposed = false;
        private readonly string _settingsFilePath = "settings.json";
        private DateTime _lastPushTime = DateTime.MinValue; // 添加上次推送时间记录
        
        // 全局推送锁，防止多实例或多线程同时推送
        private static readonly object _pushLock = new object();
        private static DateTime _globalLastPushTime = DateTime.MinValue;

        public AccountDataService(
            BinanceApiService binanceApiService, 
            IAccountRepository accountRepository,
            ILogger<AccountDataService> logger,
            NotificationService notificationService)
        {
            _binanceApiService = binanceApiService;
            _accountRepository = accountRepository;
            _logger = logger;
            _notificationService = notificationService;
        }

        /// <summary>
        /// 是否正在运行
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// 手动触发推送（用于测试）
        /// </summary>
        public async Task<bool> TriggerManualPushAsync()
        {
            try
            {
                _logger.LogInformation("🔔 手动触发推送测试");
                
                var settings = LoadNotificationSettings();
                if (!settings.IsEnabled)
                {
                    _logger.LogWarning("❌ 推送功能未启用");
                    return false;
                }

                if (string.IsNullOrEmpty(settings.XtuisToken))
                {
                    _logger.LogWarning("❌ 推送Token为空");
                    return false;
                }
                
                var balances = await _accountRepository.GetAllAccountRealTimeBalancesAsync();
                if (balances == null || balances.Count == 0)
                {
                    _logger.LogWarning("❌ 没有获取到账户余额数据");
                    return false;
                }
                
                var balanceData = balances.Select(b => (
                    AccountName: b.AccountName ?? "未知账户",
                    TotalEquity: b.TotalEquity,
                    UnrealizedPnl: b.UnrealizedPnl,
                    UpdateTime: b.Timestamp
                )).ToList();

                var message = _notificationService.FormatAccountBalancesMessage(balanceData);
                var success = await _notificationService.SendXtuisNotificationAsync(
                    settings.XtuisToken, 
                    "TCServer账户监管报告（手动触发）", 
                    message);
                    
                if (success)
                {
                    _logger.LogInformation("✅ 手动推送完成");
                    
                    // 手动推送成功后也更新全局推送时间，防止立即的定时推送
                    lock (_pushLock)
                    {
                        _globalLastPushTime = DateTime.Now;
                        _lastPushTime = DateTime.Now;
                    }
                }
                else
                {
                    _logger.LogWarning("❌ 手动推送失败");
                }
                
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "手动推送异常: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 启动定时查询
        /// </summary>
        public void StartQuery()
        {
            if (_isRunning || _disposed)
            {
                _logger.LogWarning("定时查询已在运行或服务已释放，无法启动");
                return;
            }

            try
            {
                _logger.LogInformation("🚀 启动账户数据定时查询服务");
                
                _isRunning = true;
                
                // 创建主定时器，每30秒执行一次（调试模式）
                _timer = new Timer(OnTimerCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
                
                // 创建推送检查定时器，每分钟检查一次
                _pushTimer = new Timer(OnPushTimerCallback, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
                
                _logger.LogInformation("📱 推送检查定时器已启动，将每分钟检查一次推送时间");
                
                _logger.LogInformation("✅ 账户数据定时查询服务启动成功");
                _logger.LogInformation("⏰ API查询间隔：30秒 (调试模式，正式版本为1分钟)");
                _logger.LogInformation("📱 推送检查间隔：1分钟");
                _logger.LogInformation("🔄 服务将立即执行第一次查询...");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _logger.LogError(ex, "启动账户数据定时查询服务失败: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 停止定时查询
        /// </summary>
        public void StopQuery()
        {
            if (!_isRunning)
            {
                _logger.LogWarning("定时查询未在运行，无需停止");
                return;
            }

            _isRunning = false;
            _timer?.Dispose();
            _timer = null;
            
            // 停止推送定时器
            _pushTimer?.Dispose();
            _pushTimer = null;
            
            _logger.LogInformation("=== 停止账户信息定时查询服务 ===");
            _logger.LogInformation("定时器已销毁，服务已停止");
        }

        /// <summary>
        /// 查询所有账户数据
        /// </summary>
        private async Task QueryAllAccountsData()
        {
            if (!_isRunning || _disposed)
            {
                _logger.LogWarning("服务未运行或已释放，跳过数据查询");
                return;
            }

            var startTime = DateTime.Now;
            try
            {
                _logger.LogInformation("🔄 开始查询所有账户数据... 时间: {Time}", startTime.ToString("HH:mm:ss"));

                // 获取所有账户
                _logger.LogInformation("📋 正在从数据库获取账户列表...");
                var accounts = await _accountRepository.GetAllAccountsAsync();
                
                if (accounts == null)
                {
                    _logger.LogError("❌ 获取账户列表失败：返回null");
                    return;
                }
                
                if (accounts.Count == 0)
                {
                    _logger.LogWarning("⚠️ 数据库中没有找到任何账户配置");
                    _logger.LogInformation("💡 请先在账户管理界面添加账户信息");
                    return;
                }

                _logger.LogInformation($"📋 从数据库获取到 {accounts.Count} 个账户，开始逐个查询...");

                // 打印账户详细信息用于调试
                for (int i = 0; i < accounts.Count; i++)
                {
                    var acc = accounts[i];
                    var hasApiKey = !string.IsNullOrEmpty(acc.ApiKey);
                    var hasSecretKey = !string.IsNullOrEmpty(acc.SecretKey);
                    _logger.LogInformation($"  账户 [{i + 1}]: ID={acc.AcctId}, 名称={acc.AcctName}, 备注={acc.Memo}, " +
                                         $"有API密钥={hasApiKey}, 有Secret密钥={hasSecretKey}, 状态={acc.Status}");
                }

                var successCount = 0;
                var failCount = 0;

                for (int i = 0; i < accounts.Count; i++)
                {
                    var account = accounts[i];
                    try
                    {
                        _logger.LogInformation($"🔍 [{i + 1}/{accounts.Count}] 查询账户: {account.Memo ?? account.AcctName}");
                        await QuerySingleAccountData(account);
                        successCount++;
                        _logger.LogInformation($"✅ [{i + 1}/{accounts.Count}] 账户 {account.Memo ?? account.AcctName} 数据查询成功");
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        _logger.LogError(ex, $"❌ [{i + 1}/{accounts.Count}] 账户 {account.Memo ?? account.AcctName} 数据查询失败: {ex.Message}");
                    }

                    // 延迟500ms，避免API频率限制
                    if (i < accounts.Count - 1) // 最后一个账户不需要延迟
                    {
                        await Task.Delay(500);
                    }
                }

                var endTime = DateTime.Now;
                var duration = (endTime - startTime).TotalSeconds;
                _logger.LogInformation($"🎯 账户数据查询完成! 耗时: {duration:F1}秒, 成功: {successCount}, 失败: {failCount}");
                _logger.LogInformation($"⏰ 下次查询时间: {DateTime.Now.AddSeconds(30):HH:mm:ss}");
            }
            catch (Exception ex)
            {
                var endTime = DateTime.Now;
                var duration = (endTime - startTime).TotalSeconds;
                _logger.LogError(ex, $"💥 查询所有账户数据时发生严重错误 (耗时: {duration:F1}秒): {ex.Message}");
                _logger.LogError($"错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 查询单个账户数据
        /// </summary>
        private async Task QuerySingleAccountData(AccountInfo account)
        {
            var accountName = account.Memo ?? account.AcctName;
            
            try
            {
                _logger.LogInformation($"🔍 开始查询账户 ID={account.AcctId}, 名称={accountName}");
                
                if (string.IsNullOrEmpty(account.ApiKey) || string.IsNullOrEmpty(account.SecretKey))
                {
                    _logger.LogWarning($"⚠️ 账户 {accountName} 缺少API密钥信息，跳过查询");
                    return;
                }

                _logger.LogInformation($"  📡 正在获取账户 {accountName} 的API数据...");

                // 获取账户信息
                _logger.LogDebug($"  📡 调用币安API获取账户信息...");
                var accountInfo = await _binanceApiService.GetAccountInfoAsync(account.ApiKey, account.SecretKey);
                if (accountInfo == null)
                {
                    _logger.LogError($"❌ 无法获取账户 {accountName} 的账户信息，API返回null");
                    return;
                }

                _logger.LogInformation($"  💰 账户 {accountName} API返回数据 - 总权益: {accountInfo.TotalWalletBalance:F4} USDT, 可用余额: {accountInfo.AvailableBalance:F4} USDT");

                // 获取持仓信息
                _logger.LogDebug($"  📡 调用币安API获取持仓信息...");
                var positions = await _binanceApiService.GetPositionInfoAsync(account.ApiKey, account.SecretKey);
                var validPositions = positions?.Where(p => p.PositionAmt != 0).ToList() ?? new List<BinanceApiService.PositionInfoDto>();
                
                _logger.LogInformation($"  📊 账户 {accountName} API返回持仓数据 - 总持仓: {positions?.Count ?? 0} 个, 活跃持仓: {validPositions.Count} 个");

                // 更新账户余额
                // 计算账户预估总资产：钱包余额 + 未实现盈亏（类似币安APP中的"账户预估总资产"）
                var totalEstimatedBalance = accountInfo.TotalWalletBalance + accountInfo.TotalUnrealizedProfit;
                
                var balance = new AccountBalance
                {
                    AccountId = account.AcctId,
                    TotalEquity = totalEstimatedBalance, // 使用预估总资产
                    AvailableBalance = accountInfo.AvailableBalance,
                    MarginBalance = accountInfo.TotalMarginBalance,
                    UnrealizedPnl = accountInfo.TotalUnrealizedProfit,
                    Timestamp = DateTime.Now
                };

                _logger.LogInformation($"  💾 正在更新账户 {accountName} 的余额数据到数据库...");
                try
                {
                    await _accountRepository.UpdateAccountRealTimeBalanceAsync(account.AcctId, balance);
                    _logger.LogInformation($"  ✅ 账户 {accountName} 余额数据更新成功");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"❌ 账户 {accountName} 余额数据更新失败: {ex.Message}");
                    throw;
                }

                // 更新持仓信息
                if (validPositions.Count > 0)
                {
                    var accountPositions = validPositions
                        .Select(p => new AccountPosition
                        {
                            AccountId = account.AcctId,
                            Symbol = p.Symbol,
                            PositionSide = GetActualPositionSide(p.PositionSide, p.PositionAmt),
                            EntryPrice = p.EntryPrice,
                            MarkPrice = p.MarkPrice,
                            PositionAmt = Math.Abs(p.PositionAmt), // 取绝对值
                            Leverage = (int)p.Leverage,
                            MarginType = p.MarginType == "isolated" ? "ISOLATED" : "CROSS",
                            IsolatedMargin = p.IsolatedMargin,
                            UnrealizedPnl = p.UnRealizedProfit,
                            LiquidationPrice = p.LiquidationPrice,
                            Timestamp = DateTime.Now
                        }).ToList();

                    _logger.LogInformation($"  📈 正在更新账户 {accountName} 的 {accountPositions.Count} 个持仓到数据库...");
                    
                    // 记录详细的持仓信息
                    foreach (var pos in validPositions)
                    {
                        var actualSide = GetActualPositionSide(pos.PositionSide, pos.PositionAmt);
                        _logger.LogDebug($"    持仓: {pos.Symbol}, API方向: {pos.PositionSide}, 实际方向: {actualSide}, 数量: {pos.PositionAmt}, 盈亏: {pos.UnRealizedProfit:F4} USDT");
                    }
                    
                    try
                    {
                        await _accountRepository.UpdateAccountPositionsAsync(account.AcctId, accountPositions);
                        _logger.LogInformation($"  ✅ 账户 {accountName} 持仓数据更新成功");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ 账户 {accountName} 持仓数据更新失败: {ex.Message}");
                        throw;
                    }
                }
                else
                {
                    _logger.LogInformation($"  🧹 清空账户 {accountName} 的持仓记录 (无活跃持仓)");
                    try
                    {
                        await _accountRepository.UpdateAccountPositionsAsync(account.AcctId, new List<AccountPosition>());
                        _logger.LogInformation($"  ✅ 账户 {accountName} 持仓记录清空成功");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ 账户 {accountName} 持仓记录清空失败: {ex.Message}");
                        throw;
                    }
                }
                
                _logger.LogInformation($"✅ 账户 {accountName} 数据查询和更新全部完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"💥 查询账户 {accountName} 数据时发生错误: {ex.Message}");
                _logger.LogError($"错误详情: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 根据币安API返回的持仓方向和持仓数量，判断实际的持仓方向
        /// </summary>
        /// <param name="apiPositionSide">API返回的持仓方向</param>
        /// <param name="positionAmt">持仓数量</param>
        /// <returns>实际持仓方向：LONG 或 SHORT</returns>
        private string GetActualPositionSide(string apiPositionSide, decimal positionAmt)
        {
            // 如果API明确返回LONG或SHORT，直接使用
            if (apiPositionSide == "LONG")
            {
                return "LONG";
            }
            
            if (apiPositionSide == "SHORT")
            {
                return "SHORT";
            }
            
            // 如果是BOTH（双向持仓模式）或其他值，根据持仓数量判断
            // 正数表示多头持仓，负数表示空头持仓
            if (positionAmt > 0)
            {
                return "LONG";
            }
            else if (positionAmt < 0)
            {
                return "SHORT";
            }
            else
            {
                // 如果持仓数量为0，默认返回LONG（理论上不应该出现，因为已经过滤了0持仓）
                return "LONG";
            }
        }

        /// <summary>
        /// 定时查询回调
        /// </summary>
        private async void OnTimerCallback(object? state)
        {
            if (!_isRunning || _disposed)
            {
                _logger.LogWarning("定时器触发但服务未运行或已释放，跳过执行");
                return;
            }

            try
            {
                _logger.LogInformation("🔔 === 定时器触发 === 开始执行账户数据查询");
                await QueryAllAccountsData();
                _logger.LogInformation("🔔 === 定时器执行完成 ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 定时器回调执行失败: {Message}", ex.Message);
                // 不重新抛出异常，避免崩溃
            }
        }

        /// <summary>
        /// 推送检查定时器回调
        /// </summary>
        private async void OnPushTimerCallback(object? state)
        {
            if (!_isRunning || _disposed)
            {
                _logger.LogDebug("推送检查跳过：服务未运行或已释放");
                return;
            }

            try
            {
                var now = DateTime.Now;
                _logger.LogDebug($"🕐 推送检查定时器触发 - 当前时间: {now:HH:mm:ss}");
                
                var settings = LoadNotificationSettings();
                _logger.LogDebug($"📋 推送设置状态 - 启用: {settings.IsEnabled}, Token长度: {settings.XtuisToken?.Length ?? 0}, 时间段数量: {settings.PushTimeSlots?.Count ?? 0}");
                
                if (!settings.IsEnabled)
                {
                    _logger.LogDebug("❌ 推送功能未启用，跳过检查");
                    return;
                }

                if (string.IsNullOrEmpty(settings.XtuisToken))
                {
                    _logger.LogWarning("❌ 推送Token为空，无法推送");
                    return;
                }

                if (settings.PushTimeSlots == null || settings.PushTimeSlots.Count == 0)
                {
                    _logger.LogWarning("❌ 没有配置推送时间段");
                    return;
                }

                // 详细记录每个时间段的检查情况
                bool shouldPush = false;
                var matchedSlots = new List<string>();
                
                foreach (var slot in settings.PushTimeSlots)
                {
                    var inTimeRange = now.Hour >= slot.StartHour && now.Hour <= slot.EndHour;
                    var inMinuteRange = slot.PushMinutes.Contains(now.Minute);
                    
                    _logger.LogDebug($"  时间段检查: {slot.StartHour:D2}:XX-{slot.EndHour:D2}:XX, " +
                                   $"当前时间在范围内: {inTimeRange}, " +
                                   $"分钟匹配({string.Join(",", slot.PushMinutes)}): {inMinuteRange}, " +
                                   $"启用: {slot.IsEnabled}");
                    
                    if (slot.IsEnabled && inTimeRange && inMinuteRange)
                    {
                        shouldPush = true;
                        var slotInfo = $"{slot.StartHour:D2}:XX-{slot.EndHour:D2}:XX";
                        matchedSlots.Add(slotInfo);
                        _logger.LogInformation($"✅ 匹配到推送时间段: {slotInfo}, 分钟: {now.Minute}");
                    }
                }

                // 记录所有匹配的时间段
                if (matchedSlots.Count > 0)
                {
                    _logger.LogInformation($"📅 共匹配到 {matchedSlots.Count} 个时间段: {string.Join(", ", matchedSlots)}");
                    if (matchedSlots.Count > 1)
                    {
                        _logger.LogWarning($"⚠️ 检测到时间段重叠！匹配的时间段: {string.Join(", ", matchedSlots)}");
                    }
                }

                if (shouldPush)
                {
                    // 使用全局锁确保推送操作的原子性
                    lock (_pushLock)
                    {
                        // 双重检查：防重复推送
                        var timeSinceLastPush = now - _globalLastPushTime;
                        if (timeSinceLastPush.TotalMinutes < 1)
                        {
                            _logger.LogInformation($"🔒 全局防重复：距离上次推送时间过短({timeSinceLastPush.TotalSeconds:F0}秒)，跳过推送");
                            return;
                        }

                        // 立即更新全局推送时间，防止并发推送
                        _globalLastPushTime = now;
                        _lastPushTime = now;
                    }

                    _logger.LogInformation("🔔 推送时间到达，开始推送账户余额信息");
                    
                    var balances = await _accountRepository.GetAllAccountRealTimeBalancesAsync();
                    _logger.LogInformation($"📊 获取到 {balances?.Count ?? 0} 个账户的余额数据");
                    
                    if (balances == null || balances.Count == 0)
                    {
                        _logger.LogWarning("❌ 没有获取到账户余额数据，无法推送");
                        return;
                    }
                    
                    var balanceData = balances.Select(b => (
                        AccountName: b.AccountName ?? "未知账户",
                        TotalEquity: b.TotalEquity,
                        UnrealizedPnl: b.UnrealizedPnl,
                        UpdateTime: b.Timestamp
                    )).ToList();

                    // 格式化推送消息
                    var message = _notificationService.FormatAccountBalancesMessage(balanceData);
                    _logger.LogDebug($"📝 推送消息内容预览: {message.Substring(0, Math.Min(100, message.Length))}...");
                    
                    // 发送推送通知
                    var success = await _notificationService.SendXtuisNotificationAsync(
                        settings.XtuisToken, 
                        "TCServer账户监管报告", 
                        message);
                        
                    if (success)
                    {
                        _logger.LogInformation($"✅ 账户余额推送完成 - 推送时间: {now:HH:mm:ss}");
                    }
                    else
                    {
                        _logger.LogWarning("❌ 账户余额推送失败");
                        
                        // 推送失败时重置时间，允许稍后重试
                        lock (_pushLock)
                        {
                            _globalLastPushTime = DateTime.MinValue;
                            _lastPushTime = DateTime.MinValue;
                        }
                    }
                }
                else
                {
                    _logger.LogDebug($"⏰ 当前时间 {now:HH:mm} 不在推送时间段内");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "推送检查回调执行失败: {Message}", ex.Message);
                // 不重新抛出异常，避免崩溃
            }
        }

        /// <summary>
        /// 加载推送设置
        /// </summary>
        private NotificationSettings LoadNotificationSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    
                    // 首先尝试反序列化为AppSettings
                    var appSettings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (appSettings?.NotificationSettings != null)
                    {
                        _logger.LogDebug("成功从AppSettings加载推送设置");
                        return appSettings.NotificationSettings;
                    }
                    
                    // 如果不是AppSettings格式，尝试动态反序列化（兼容旧格式）
                    var settings = JsonConvert.DeserializeObject<dynamic>(json);
                    if (settings?.NotificationSettings != null)
                    {
                        var notificationJson = settings.NotificationSettings.ToString();
                        _logger.LogDebug("从动态格式加载推送设置");
                        return JsonConvert.DeserializeObject<NotificationSettings>(notificationJson) ?? new NotificationSettings();
                    }
                    
                    _logger.LogWarning("配置文件中未找到推送设置");
                }
                else
                {
                    _logger.LogDebug("配置文件不存在");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载推送设置失败: {Message}", ex.Message);
            }
            return new NotificationSettings();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            StopQuery();
            _timer?.Dispose();
            _pushTimer?.Dispose();
            _disposed = true;
        }
    }
} 