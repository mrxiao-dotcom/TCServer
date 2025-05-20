using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using TCServer.Common.Interfaces;
using TCServer.Common.Models;
using System.Threading;
using System.Timers;

namespace TCServer.Core.Services
{
    public class RankingService : IDisposable
    {
        private readonly IKlineRepository _klineRepository;
        private readonly IDailyRankingRepository _rankingRepository;
        private readonly BinanceApiService _binanceApiService;
        private readonly ILogger<RankingService> _logger;
        private readonly int _rankingCount = 10; // 默认排名数量
        private bool _isCalculating = false; // 添加计算状态标志
        private readonly object _calculationLock = new object(); // 添加锁对象
        private CancellationTokenSource? _cancellationTokenSource; // 添加取消令牌源
        
        // 添加实时排名相关字段
        private System.Timers.Timer? _realtimeUpdateTimer;
        private System.Timers.Timer? _dailyResetTimer;
        private readonly object _realtimeLock = new object();
        private bool _isRealtimeUpdateEnabled = false;
        
        // 添加日志事件
        public event EventHandler<RankingLogEventArgs>? LogUpdated;
        
        // 添加排名更新事件
        public event EventHandler<RankingUpdateEventArgs>? RankingUpdated;
        
        public RankingService(
            IKlineRepository klineRepository, 
            IDailyRankingRepository rankingRepository,
            BinanceApiService binanceApiService,
            ILogger<RankingService> logger)
        {
            _klineRepository = klineRepository;
            _rankingRepository = rankingRepository;
            _binanceApiService = binanceApiService;
            _logger = logger;
            
            // 只初始化定时器，不启动
            InitializeTimers();
        }
        
        private void InitializeTimers()
        {
            // 实时更新定时器（3秒间隔，提高更新频率）
            _realtimeUpdateTimer = new System.Timers.Timer(3000);
            _realtimeUpdateTimer.Elapsed += async (s, e) => 
            {
                if (!_isRealtimeUpdateEnabled) return;
                
                try 
                {
                    await UpdateRealtimeRankingAsync();
                }
                catch (Exception ex)
                {
                    Log($"实时排名更新出错: {ex.Message}", LogLevel.Error);
                }
            };
            
            // 每日重置定时器（每分钟检查一次）
            _dailyResetTimer = new System.Timers.Timer(60000);
            _dailyResetTimer.Elapsed += async (s, e) => 
            {
                try 
                {
                    await CheckAndResetDailyRankingAsync();
                }
                catch (Exception ex)
                {
                    Log($"每日重置检查出错: {ex.Message}", LogLevel.Error);
                }
            };
            // 移除自动启动
            // _dailyResetTimer.Start();
        }
        
        private async Task CheckAndResetDailyRankingAsync()
        {
            var now = DateTime.Now;
            
            // 23:50 停止更新
            if (now.Hour == 23 && now.Minute == 50 && _isRealtimeUpdateEnabled)
            {
                await StopRealtimeUpdateAsync();
                Log("已停止实时排名更新（23:50）", LogLevel.Information);
            }
            
            // 0:05 重新开始
            if (now.Hour == 0 && now.Minute == 5 && !_isRealtimeUpdateEnabled)
            {
                // 删除前一天的排名记录
                var yesterday = now.Date.AddDays(-1);
                await _rankingRepository.DeleteRankingForDateAsync(yesterday);
                Log($"已删除 {yesterday:yyyy-MM-dd} 的排名记录", LogLevel.Information);
                
                // 重新开始实时更新
                await StartRealtimeUpdateAsync();
                Log("已开始新的实时排名更新（0:05）", LogLevel.Information);
            }
        }
        
        public async Task StartRealtimeUpdateAsync()
        {
            if (_isRealtimeUpdateEnabled)
                return;

            try
            {
                // 先获取一次数据，确保服务正常
                var symbols = await _binanceApiService.GetPerpetualSymbolsAsync();
                if (symbols == null || !symbols.Any())
                {
                    Log("无法获取交易对信息，启动失败", LogLevel.Error);
                    return;
                }

                // 启动每日重置定时器
                _dailyResetTimer?.Start();

                // 使用 Task.Run 来确保异步操作在后台线程执行
                await Task.Run(() =>
                {
                    lock (_realtimeLock)
                    {
                        _isRealtimeUpdateEnabled = true;
                        _realtimeUpdateTimer?.Start();
                    }
                });
                
                Log("实时排名更新已启动", LogLevel.Information);
                // 立即执行一次更新
                await UpdateRealtimeRankingAsync();
            }
            catch (Exception ex)
            {
                // 如果启动失败，确保停止定时器
                _dailyResetTimer?.Stop();
                Log($"启动实时排名更新失败: {ex.Message}", LogLevel.Error);
                throw;
            }
        }
        
        public async Task StopRealtimeUpdateAsync()
        {
            if (!_isRealtimeUpdateEnabled)
                return;

            lock (_realtimeLock)
            {
                _isRealtimeUpdateEnabled = false;
                _realtimeUpdateTimer?.Stop();
                _dailyResetTimer?.Stop();  // 同时停止每日重置定时器
            }
            
            Log("实时排名更新已停止", LogLevel.Information);
        }
        
        private async Task UpdateRealtimeRankingAsync()
        {
            if (!_isRealtimeUpdateEnabled)
                return;

            const int maxRetries = 3;
            int currentRetry = 0;
            
            while (currentRetry < maxRetries)
            {
                try
                {
                    var now = DateTime.Now;
                    var today = now.Date;
                    
                    // 获取所有交易对
                    var symbols = await _binanceApiService.GetPerpetualSymbolsAsync();
                    if (symbols == null || !symbols.Any())
                    {
                        Log("获取交易对信息为空，等待下次更新", LogLevel.Warning);
                        return;
                    }

                    var rankings = new List<(string Symbol, decimal Percentage)>();
                    
                    // 获取每个交易对的K线数据
                    foreach (var symbol in symbols)
                    {
                        try
                        {
                            // 获取最新的一条K线数据
                            var klines = await _binanceApiService.GetKlinesAsync(
                                symbol,
                                "1d",
                                DateTime.Now.AddDays(-1),
                                DateTime.Now);
                                
                            if (klines.Count > 0)
                            {
                                var latestKline = klines[0];
                                var previousKline = klines.Count > 1 ? klines[1] : null;
                                
                                if (previousKline != null)
                                {
                                    var percentage = (latestKline.Close - previousKline.Open) / previousKline.Open * 100;
                                    rankings.Add((symbol, percentage));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"获取 {symbol} 数据时出错: {ex.Message}", LogLevel.Warning);
                        }
                        
                        // 添加延迟以避免请求过于频繁
                        await Task.Delay(100);
                    }

                    if (!rankings.Any())
                    {
                        Log("没有有效的排名数据，等待下次更新", LogLevel.Warning);
                        return;
                    }

                    // 获取涨幅前N和跌幅前N
                    var topGainers = rankings
                        .OrderByDescending(r => r.Percentage)
                        .Take(_rankingCount)
                        .Select((r, index) => new RankingItem
                        {
                            Rank = index + 1,
                            Symbol = r.Symbol,
                            Percentage = r.Percentage / 100m  // 转换为小数
                        })
                        .ToList();

                    var topLosers = rankings
                        .OrderBy(r => r.Percentage)
                        .Take(_rankingCount)
                        .Select((r, index) => new RankingItem
                        {
                            Rank = index + 1,
                            Symbol = r.Symbol,
                            Percentage = r.Percentage / 100m  // 转换为小数
                        })
                        .ToList();

                    // 保存排名数据并等待完成
                    await SaveRankingDataAsync(today, topGainers, topLosers);
                    
                    // 触发排名更新事件
                    OnRankingUpdated(new RankingUpdateEventArgs
                    {
                        Date = today,
                        TopGainers = topGainers,
                        TopLosers = topLosers,
                        Timestamp = DateTime.Now
                    });
                    
                    // 成功完成，跳出重试循环
                    break;
                }
                catch (Exception ex)
                {
                    currentRetry++;
                    var errorMessage = ex is AggregateException aggregateEx 
                        ? string.Join(", ", aggregateEx.InnerExceptions.Select(e => e.Message))
                        : ex.Message;
                        
                    Log($"更新实时排名时出错 (尝试 {currentRetry}/{maxRetries}): {errorMessage}", LogLevel.Error);
                    
                    if (ex.InnerException != null)
                    {
                        Log($"内部异常: {ex.InnerException.Message}", LogLevel.Error);
                    }
                    
                    if (currentRetry >= maxRetries)
                    {
                        Log("达到最大重试次数，等待下次更新周期", LogLevel.Error);
                        return;
                    }
                    
                    // 等待一段时间后重试，使用指数退避策略
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry));
                    Log($"等待 {delay.TotalSeconds} 秒后重试...", LogLevel.Information);
                    await Task.Delay(delay);
                }
            }
        }
        
        /// <summary>
        /// 取消正在进行的计算
        /// </summary>
        public void CancelCalculation()
        {
            _cancellationTokenSource?.Cancel();
            Log("排名计算已取消", LogLevel.Warning);
        }
        
        /// <summary>
        /// 计算指定日期的涨跌幅排名
        /// </summary>
        public async Task<bool> CalculateDailyRankingAsync(DateTime date, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                

                
                Log($"开始计算 {date:yyyy-MM-dd} 的排名数据...");
                
                // 检查是否已存在该日期的排名
                bool exists = await _rankingRepository.HasRankingForDateAsync(date);
                if (exists)
                {
                    string message = $"{date:yyyy-MM-dd}的排名数据已存在，跳过计算";
                    _logger.LogInformation(message);
                    Log(message);
                    return true;
                }
                
                Log($"正在获取所有交易对...");
                
                // 获取所有交易对
                var symbols = await _klineRepository.GetAllSymbolsAsync();
                Log($"共获取到{symbols.Count()}个交易对");
                
                Log($"正在获取每个交易对的K线数据...");
                var tasks = symbols.Select(async symbol => 
                {
                    try
                    {
                        // 获取当天的K线数据
                        var klineData = await _klineRepository.GetKlineDataListAsync(
                            symbol, 
                            date.Date,  // 当天00:00:00
                            date.Date.AddDays(1).AddSeconds(-1));  // 当天23:59:59
                            
                        // 如果没有当天的数据，尝试获取前一天的
                        if (!klineData.Any())
                        {
                            klineData = await _klineRepository.GetKlineDataListAsync(
                                symbol,
                                date.Date.AddDays(-1),  // 前一天00:00:00
                                date.Date.AddSeconds(-1));  // 前一天23:59:59
                        }
                        
                        return klineData.FirstOrDefault();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"获取{symbol}的K线数据时出错");
                        return null;
                    }
                });
                
                var klines = (await Task.WhenAll(tasks))
                    .Where(k => k != null)
                    .ToList();
                
                Log($"获取到{klines.Count}个有效K线数据");
                
                if (klines.Count == 0)
                {
                    string message = $"{date:yyyy-MM-dd}没有K线数据，无法计算排名";
                    _logger.LogWarning(message);
                    Log(message, LogLevel.Warning);
                    return false;
                }
                
                // 计算涨跌幅
                Log("开始计算涨跌幅...");
                var rankings = klines.Select(k => 
                {
                    decimal percentage = 0;
                    if (k.OpenPrice != 0)
                    {
                        percentage = (k.ClosePrice - k.OpenPrice) / k.OpenPrice;
                    }
                    
                    return new
                    {
                        Symbol = k.Symbol,
                        Percentage = percentage,
                        OpenTime = k.OpenTime
                    };
                })
                .Where(r => r.OpenTime.Date == date.Date)  // 只使用当天的数据
                .ToList();
                
                // 获取涨幅前N和跌幅前N
                Log($"计算涨幅前{_rankingCount}名...");
                var topGainers = rankings
                    .OrderByDescending(r => r.Percentage)
                    .Take(_rankingCount)
                    .Select((r, index) => new RankingItem
                    {
                        Rank = index + 1,
                        Symbol = r.Symbol,
                        Percentage = r.Percentage
                    })
                    .ToList();
                
                Log($"计算跌幅前{_rankingCount}名...");
                var topLosers = rankings
                    .OrderBy(r => r.Percentage)
                    .Take(_rankingCount)
                    .Select((r, index) => new RankingItem
                    {
                        Rank = index + 1,
                        Symbol = r.Symbol,
                        Percentage = r.Percentage
                    })
                    .ToList();
                
                // 格式化结果并保存
                Log("格式化排名数据...");
                string topGainersStr = string.Join("|", topGainers.Select(g => g.ToString()));
                string topLosersStr = string.Join("|", topLosers.Select(l => l.ToString()));
                
                var ranking = new DailyRanking
                {
                    Date = date.Date,
                    TopGainers = topGainersStr,
                    TopLosers = topLosersStr,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                
                // 输出SQL语句和参数（模拟）
                string sql = "INSERT INTO daily_ranking (date, top_gainers, top_losers, created_at, updated_at) VALUES (@Date, @TopGainers, @TopLosers, @CreatedAt, @UpdatedAt) ON DUPLICATE KEY UPDATE top_gainers = VALUES(top_gainers), top_losers = VALUES(top_losers), updated_at = VALUES(updated_at)";
                string sqlLog = $"SQL语句: {sql}，参数: Date={ranking.Date}, TopGainers={ranking.TopGainers}, TopLosers={ranking.TopLosers}, CreatedAt={ranking.CreatedAt}, UpdatedAt={ranking.UpdatedAt}";
                _logger.LogInformation(sqlLog);
                Log(sqlLog, LogLevel.Information);
                
                Log("保存排名数据到数据库...");
                bool success = await _rankingRepository.SaveRankingAsync(ranking);
                if (success)
                {
                    string message = $"成功计算并保存{date:yyyy-MM-dd}的排名数据";
                    _logger.LogInformation(message);
                    Log(message);
                }
                else
                {
                    string message = $"保存{date:yyyy-MM-dd}的排名数据失败";
                    _logger.LogError(message);
                    Log(message, LogLevel.Error);
                }
                
                return success;
            }
            catch (OperationCanceledException)
            {
                Log($"计算 {date:yyyy-MM-dd} 的排名数据被取消", LogLevel.Warning);
                throw;
            }
            catch (Exception ex)
            {
                string message = $"计算{date:yyyy-MM-dd}的排名时发生错误: {ex.Message}";
                _logger.LogError(ex, message);
                Log(message, LogLevel.Error);
                
                // 详细记录内部异常
                if (ex.InnerException != null)
                {
                    string innerMessage = $"内部异常: {ex.InnerException.Message}";
                    _logger.LogError(innerMessage);
                    Log(innerMessage, LogLevel.Error);
                    
                    // 如果还有嵌套异常，继续记录
                    if (ex.InnerException.InnerException != null)
                    {
                        string nestedMessage = $"嵌套异常: {ex.InnerException.InnerException.Message}";
                        _logger.LogError(nestedMessage);
                        Log(nestedMessage, LogLevel.Error);
                    }
                }
                
                // 记录异常类型和详细信息
                string exceptionDetails = $"异常类型: {ex.GetType().FullName}, 消息: {ex.Message}";
                _logger.LogError(exceptionDetails);
                Log(exceptionDetails, LogLevel.Error);
                
                Log($"异常堆栈: {ex.StackTrace}", LogLevel.Error);
                
                return false;
            }
        }
        
        /// <summary>
        /// 计算最近N天的排名
        /// </summary>
        public async Task<int> CalculateRecentRankingsAsync(int days)
        {
            try
            {
                Log($"开始计算最近{days}天的排名数据...");
                
                var endDate = DateTime.Now.Date;
                var startDate = endDate.AddDays(-days);
                
                // 获取需要计算的日期
                var dates = await _rankingRepository.GetDatesNeedingRankingCalculationAsync(startDate, endDate);
                
                string message = $"需要计算{dates.Count}天的排名数据";
                _logger.LogInformation(message);
                Log(message);
                
                int successCount = 0;
                foreach (var date in dates)
                {
                    Log($"开始计算 {date:yyyy-MM-dd} 日期的排名...");
                    bool success = await CalculateDailyRankingAsync(date);
                    if (success)
                    {
                        successCount++;
                    }
                    Log($"{date:yyyy-MM-dd} 排名计算{(success ? "成功" : "失败")}");
                }
                
                message = $"成功计算了{successCount}天的排名数据";
                _logger.LogInformation(message);
                Log(message);
                return successCount;
            }
            catch (Exception ex)
            {
                string message = $"计算最近排名时发生错误: {ex.Message}";
                _logger.LogError(ex, message);
                Log(message, LogLevel.Error);
                return 0;
            }
        }
        
        /// <summary>
        /// 检查并计算缺失的排名数据
        /// </summary>
        public async Task<bool> CheckAndCalculateMissingRankingsAsync()
        {
            // 使用锁确保同一时间只有一个计算任务在执行
            lock (_calculationLock)
            {
                if (_isCalculating)
                {
                    Log("已有排名计算任务正在执行，跳过本次计算", LogLevel.Warning);
                    return false;
                }
                _isCalculating = true;
                _cancellationTokenSource = new CancellationTokenSource();
            }

            try
            {
                Log("开始检查并计算缺失的排名数据...");
                
                var today = DateTime.Now.Date;
                var yesterday = today.AddDays(-1);
                
                // 获取最近30天的日期范围（从30天前到昨天）
                var startDate = yesterday.AddDays(-29);  // 30天前
                
                Log($"检查历史排名数据范围: {startDate:yyyy-MM-dd} 至 {yesterday:yyyy-MM-dd}");
                
                // 创建需要检查的日期列表（从旧到新）
                var allDates = new List<DateTime>();
                for (var date = startDate; date <= yesterday; date = date.AddDays(1))
                {
                    allDates.Add(date);
                }
                
                // 获取已经有排名的日期
                var existingDates = new List<DateTime>();
                try 
                {
                    // 使用仓储层获取已存在的排名日期
                    var existingRankings = await _rankingRepository.GetRecentRankingsAsync(30);
                    existingDates = existingRankings
                        .Select(r => r.Date.Date)
                        .Distinct()
                        .ToList();
                        
                    Log($"已存在 {existingDates.Count} 天的排名数据");
                }
                catch (Exception ex)
                {
                    Log($"获取已存在排名数据时出错: {ex.Message}", LogLevel.Error);
                }
                
                // 计算需要计算的日期（排除已存在的日期）
                var datesToCalculate = allDates.Except(existingDates).ToList();
                
                if (!datesToCalculate.Any())
                {
                    Log("没有发现缺失的历史排名数据", LogLevel.Information);
                    return true;
                }
                
                int totalDays = datesToCalculate.Count;
                int successCount = 0;
                int failedCount = 0;
                
                Log($"开始计算 {totalDays} 天的历史排名数据（从最早的日期开始）...");
                
                // 从最早的日期开始计算
                foreach (var date in datesToCalculate.OrderBy(d => d))
                {
                    if (_cancellationTokenSource?.Token.IsCancellationRequested ?? false)
                    {
                        Log("计算任务被取消", LogLevel.Warning);
                        break;
                    }
                    
                    try
                    {
                        Log($"正在计算 {date:yyyy-MM-dd} 的排名数据 ({successCount + failedCount + 1}/{totalDays})...");
                        bool success = await CalculateDailyRankingAsync(date, _cancellationTokenSource?.Token ?? CancellationToken.None);
                        
                        if (success)
                        {
                            successCount++;
                            Log($"成功计算 {date:yyyy-MM-dd} 的排名数据，进度：{successCount}/{totalDays}，成功率：{(double)successCount/(successCount+failedCount)*100:F2}%", LogLevel.Information);
                        }
                        else
                        {
                            failedCount++;
                            Log($"计算 {date:yyyy-MM-dd} 的排名数据失败，已失败：{failedCount}/{totalDays}", LogLevel.Error);
                        }
                        
                        // 添加延迟，避免数据库压力过大，成功时1秒延迟
                        await Task.Delay(1000, _cancellationTokenSource?.Token ?? CancellationToken.None);
                        
                        // 添加垃圾回收，帮助释放资源
                        if ((successCount + failedCount) % 5 == 0)
                        {
                            GC.Collect();
                            Log($"已处理 {successCount + failedCount}/{totalDays} 天数据，执行垃圾回收", LogLevel.Information);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log("计算任务被取消", LogLevel.Warning);
                        break;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Log($"计算 {date:yyyy-MM-dd} 的排名数据时发生错误: {ex.Message}", LogLevel.Error);
                        if (ex.InnerException != null)
                        {
                            Log($"内部异常: {ex.InnerException.Message}", LogLevel.Error);
                        }
                        
                        // 发生错误时等待更长时间 (5秒)
                        await Task.Delay(5000, _cancellationTokenSource?.Token ?? CancellationToken.None);
                        
                        // 错误后立即执行垃圾回收
                        GC.Collect();
                        Log("错误后执行垃圾回收", LogLevel.Information);
                    }
                }
                
                // 输出统计信息
                double successRate = totalDays > 0 ? (double)successCount / totalDays * 100 : 0;
                Log($"历史排名计算完成。总计: {totalDays} 天, 成功: {successCount} 天, 失败: {failedCount} 天, 成功率: {successRate:F2}%", 
                    successRate >= 90 ? LogLevel.Information : LogLevel.Warning);
                
                return successCount > 0;
            }
            catch (Exception ex)
            {
                Log($"检查并计算历史排名时发生错误: {ex.Message}", LogLevel.Error);
                if (ex.InnerException != null)
                {
                    Log($"内部异常: {ex.InnerException.Message}", LogLevel.Error);
                }
                return false;
            }
            finally
            {
                // 主动进行垃圾回收
                GC.Collect();
                
                lock (_calculationLock)
                {
                    _isCalculating = false;
                    if (_cancellationTokenSource != null)
                    {
                        _cancellationTokenSource.Dispose();
                        _cancellationTokenSource = null;
                    }
                }
            }
        }
        
        /// <summary>
        /// 在K线数据更新后执行排名计算
        /// </summary>
        public async Task CalculateRankingAfterKlineUpdateAsync()
        {
            try
            {
                Log("K线数据更新后开始检查排名数据...");
                await CheckAndCalculateMissingRankingsAsync();
            }
            catch (Exception ex)
            {
                Log($"K线数据更新后执行排名计算时发生错误: {ex.Message}", LogLevel.Error);
                if (ex.InnerException != null)
                {
                    Log($"内部异常: {ex.InnerException.Message}", LogLevel.Error);
                }
            }
        }
        
        private async Task SaveRankingDataAsync(DateTime date, List<RankingItem> topGainers, List<RankingItem> topLosers)
        {
            try
            {
                // 格式化排名数据
                string topGainersStr = string.Join("|", topGainers.Select(g => g.ToString()));
                string topLosersStr = string.Join("|", topLosers.Select(l => l.ToString()));
                
                var ranking = new DailyRanking
                {
                    Date = date.Date,
                    TopGainers = topGainersStr,
                    TopLosers = topLosersStr,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                
                // 保存到数据库
                await _rankingRepository.SaveRankingAsync(ranking);
                
                // 触发实时排名更新事件
                RankingUpdated?.Invoke(this, new RankingUpdateEventArgs
                {
                    Date = date,
                    TopGainers = topGainers,
                    TopLosers = topLosers,
                    Timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                Log($"保存排名数据时出错: {ex.Message}", LogLevel.Error);
                throw;
            }
        }

        private void OnRankingUpdated(RankingUpdateEventArgs args)
        {
            RankingUpdated?.Invoke(this, args);
        }

        // 日志方法，同时发送事件通知UI
        private void Log(string message, LogLevel level = LogLevel.Information)
        {
            LogUpdated?.Invoke(this, new RankingLogEventArgs 
            { 
                Message = message, 
                LogLevel = level,
                Timestamp = DateTime.Now
            });
        }

        public void Dispose()
        {
            _realtimeUpdateTimer?.Dispose();
            _dailyResetTimer?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
    
    // 排名更新事件参数类
    public class RankingUpdateEventArgs : EventArgs
    {
        public DateTime Date { get; set; }
        public List<RankingItem> TopGainers { get; set; } = new();
        public List<RankingItem> TopLosers { get; set; } = new();
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    // 排名服务日志事件参数
    public class RankingLogEventArgs : EventArgs
    {
        public string Message { get; set; } = string.Empty;
        public LogLevel LogLevel { get; set; } = LogLevel.Information;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        
        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss}] [{LogLevel}] {Message}";
        }
    }
} 