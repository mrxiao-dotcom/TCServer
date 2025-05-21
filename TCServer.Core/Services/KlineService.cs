using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TCServer.Common.Models;
using TCServer.Common.Interfaces;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace TCServer.Core.Services;

public class KlineService
{
    private readonly IKlineRepository _klineRepository;
    private readonly ISystemConfigRepository _configRepository;
    private readonly ILogger<KlineService> _logger;
    private readonly BinanceApiService _binanceApiService;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _fetchTask;
    private volatile bool _isFetching = false;
    
    // 进度跟踪
    public int TotalSymbols { get; private set; }
    public int ProcessedSymbols { get; private set; }
    public string CurrentSymbol { get; private set; } = string.Empty;
    public int CurrentBatch { get; private set; }
    public int TotalBatches { get; private set; }
    
    // 进度更新事件
    public event EventHandler<ProgressUpdateEventArgs>? ProgressUpdated;

    public KlineService(
        IKlineRepository klineRepository,
        ISystemConfigRepository configRepository,
        BinanceApiService binanceApiService,
        ILogger<KlineService> logger)
    {
        _klineRepository = klineRepository;
        _configRepository = configRepository;
        _logger = logger;
        _binanceApiService = binanceApiService;
    }

    public async Task StartAsync()
    {
        if (_fetchTask != null && !_fetchTask.IsCompleted)
        {
            _logger.LogWarning("K线数据获取服务已经在运行中");
            return;
        }

        await Task.Yield();
        
            _cancellationTokenSource = new CancellationTokenSource();
        _isFetching = true;  // 设置获取状态为true
        _fetchTask = Task.Run(async () => 
        {
            try 
            {
                // 立即执行一次数据获取
            await FetchKlineDataAsync(_cancellationTokenSource.Token);
                // 然后开始定时任务循环
                await FetchKlineDataLoopAsync(_cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
                _logger.LogInformation("K线数据获取服务已取消");
        }
        catch (Exception ex)
        {
                _logger.LogError(ex, "K线数据获取服务发生错误");
            }
            finally
            {
            _isFetching = false;
        }
        });
        _logger.LogInformation("K线数据获取服务已启动，开始首次数据获取");
    }

    public async Task StopAsync()
    {
        if (_cancellationTokenSource == null)
        {
            _logger.LogWarning("K线数据获取服务未在运行");
            return;
        }

        _logger.LogInformation("正在停止K线数据获取服务...");
        
        try
        {
            // 先取消令牌
        _cancellationTokenSource.Cancel();
        
            // 等待当前获取任务完成，最多等待10秒
        if (_fetchTask != null)
        {
            try
            {
                    await Task.WhenAny(_fetchTask, Task.Delay(10000));
                    
                    // 如果任务还在运行，记录警告
                    if (!_fetchTask.IsCompleted)
                    {
                        _logger.LogWarning("等待数据获取任务超时，强制停止服务");
                    }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "等待数据获取任务结束时发生错误");
            }
        }
        }
        finally
        {
            // 确保资源被释放
            if (_cancellationTokenSource != null)
            {
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;
            }
            
        _fetchTask = null;
        _isFetching = false;
        _logger.LogInformation("K线数据获取服务已停止");
        }
    }

    private async Task FetchKlineDataLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 获取配置的执行时间
                var config = await _configRepository.GetConfigAsync("KlineFetchTime");
                TimeSpan fetchTime;
                
                if (config == null || string.IsNullOrEmpty(config.Value))
                {
                    _logger.LogWarning("未找到K线获取时间配置，使用默认值 03:00:00");
                    fetchTime = TimeSpan.Parse("03:00:00");
                    
                    // 尝试保存默认配置
                    try 
                    {
                        await _configRepository.SaveConfigAsync(new SystemConfig 
                        { 
                            Key = "KlineFetchTime", 
                            Value = "03:00:00",
                            Description = "每日K线获取时间（默认值）",
                            CreatedAt = DateTime.Now
                        });
                        _logger.LogInformation("已保存默认K线获取时间配置");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "保存默认K线获取时间配置失败");
                    }
                }
                else
                {
                try 
                {
                    fetchTime = TimeSpan.Parse(config.Value);
                        _logger.LogInformation($"使用配置的K线获取时间: {fetchTime:hh\\:mm\\:ss}");
                }
                catch (Exception)
                {
                        _logger.LogError($"K线获取时间格式错误: {config.Value}，使用默认值 03:00:00");
                    fetchTime = TimeSpan.Parse("03:00:00");
                    }
                }
                
                // 计算下次执行时间
                var now = DateTime.Now;
                var nextFetchTime = now.Date.Add(fetchTime);
                
                // 如果当前时间已经过了今天的执行时间，则设置为明天的执行时间
                if (now >= nextFetchTime)
                {
                    nextFetchTime = nextFetchTime.AddDays(1);
                }

                var delay = nextFetchTime - now;
                _logger.LogInformation($"下次获取时间：{nextFetchTime:yyyy-MM-dd HH:mm:ss}，距离现在还有 {delay.TotalHours:F1} 小时");
                
                // 等待到执行时间
                try
                {
                await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("等待过程被取消");
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("服务已被取消，退出循环");
                    break;
                }

                // 执行数据获取
                _logger.LogInformation($"到达执行时间 {DateTime.Now:HH:mm:ss}，开始获取数据");
                await FetchKlineDataAsync(cancellationToken);
                
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("数据获取过程被取消");
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("服务已被取消");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取K线数据时发生错误");
                // 发生错误后等待5分钟再重试
                try
                {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        
        _logger.LogInformation("K线数据获取循环已结束");
    }

    private async Task FetchKlineDataAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("开始获取K线数据...");
        
        try
        {
            // 首先检查今天的数据是否已经获取
            var today = DateTime.Now.Date;
            var hasTodayData = await CheckTodayDataExistsAsync();
            if (hasTodayData)
            {
                _logger.LogInformation($"今天({today:yyyy-MM-dd})的数据已经获取，跳过本次更新");
                return;
            }
        
        ProcessedSymbols = 0;
        CurrentBatch = 0;

        // 获取所有永续合约交易对
            _logger.LogInformation("正在获取交易对信息...");
        var symbols = await _binanceApiService.GetPerpetualSymbolsAsync();
            if (symbols == null || symbols.Count == 0)
        {
                _logger.LogError("获取交易对信息失败或返回空列表");
            return;
        }

        TotalSymbols = symbols.Count;
            _logger.LogInformation($"共获取到{TotalSymbols}个交易对: {string.Join(", ", symbols.Take(5))}...");
        
        // 更新批次大小设置为50
        var batchSizeConfig = await _configRepository.GetConfigAsync("BatchSize");
        int batchSize = 50;
        if (batchSizeConfig != null && !string.IsNullOrEmpty(batchSizeConfig.Value) && int.TryParse(batchSizeConfig.Value, out int configBatchSize))
        {
            batchSize = configBatchSize;
                _logger.LogInformation($"使用配置的批次大小: {batchSize}");
        }
        else
        {
                _logger.LogInformation($"使用默认批次大小: {batchSize}");
            // 保存默认批次大小
            try
            {
                await _configRepository.SaveConfigAsync(new SystemConfig
                {
                    Key = "BatchSize",
                    Value = batchSize.ToString(),
                    Description = "每批次处理的交易对数量",
                    CreatedAt = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存默认批次大小配置失败");
            }
        }

        // 使用变量存储集合的数量，避免方法组错误
        int symbolCount = symbols.Count;
        TotalBatches = (symbolCount + batchSize - 1) / batchSize; // 计算总批次数
            _logger.LogInformation($"将分{TotalBatches}批处理{symbolCount}个交易对");
        
        for (int i = 0; i < symbolCount; i += batchSize)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("K线数据获取已取消");
                break;
            }
            
            CurrentBatch++;
            var batch = symbols.Skip(i).Take(batchSize).ToList();
            
            // 更新进度信息
                _logger.LogInformation($"正在处理第{CurrentBatch}/{TotalBatches}批交易对，当前批次包含{batch.Count}个交易对: {string.Join(", ", batch.Take(3))}...");
            OnProgressUpdated(new ProgressUpdateEventArgs
            {
                TotalSymbols = TotalSymbols,
                ProcessedSymbols = ProcessedSymbols,
                CurrentBatch = CurrentBatch,
                TotalBatches = TotalBatches,
                CurrentSymbol = string.Empty,
                Message = $"批次进度：{CurrentBatch}/{TotalBatches}"
            });
            
            await ProcessSymbolBatchAsync(batch, cancellationToken);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("K线数据获取完成");
            OnProgressUpdated(new ProgressUpdateEventArgs
            {
                TotalSymbols = TotalSymbols,
                ProcessedSymbols = TotalSymbols,
                CurrentBatch = TotalBatches,
                TotalBatches = TotalBatches,
                CurrentSymbol = string.Empty,
                Message = "数据获取完成"
            });
            
            // 调用排名计算服务
            try
            {
                var serviceProvider = ServiceProviderAccessor.ServiceProvider;
                if (serviceProvider != null)
                {
                    var rankingService = serviceProvider.GetService<RankingService>();
                    if (rankingService != null)
                    {
                        _logger.LogInformation("开始计算排名数据");
                        await rankingService.CalculateRankingAfterKlineUpdateAsync();
                        _logger.LogInformation("排名数据计算完成");
                    }
                    else
                    {
                        _logger.LogWarning("无法获取RankingService实例，跳过排名计算");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调用排名计算服务时发生错误");
            }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取K线数据过程中发生错误");
            throw;
        }
    }

    private async Task ProcessSymbolBatchAsync(List<string> symbols, CancellationToken cancellationToken)
    {
        // 创建并行处理任务
        var options = new ParallelOptions 
        { 
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4), // 限制并行度
            CancellationToken = cancellationToken 
        };
        
        _logger.LogInformation($"开始并行处理批次，并行度: {options.MaxDegreeOfParallelism}");
        
        var tasks = new List<Task>();
        var syncLock = new object();
        
        foreach (var symbol in symbols)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
                
            var task = Task.Run(async () => 
            {
                int symbolIndex;
                lock (syncLock)
                {
                    ProcessedSymbols++;
                    symbolIndex = ProcessedSymbols;
                }
                
                try
                {
                    CurrentSymbol = symbol;
                    
                    // 更新进度信息
                    OnProgressUpdated(new ProgressUpdateEventArgs
                    {
                        TotalSymbols = TotalSymbols,
                        ProcessedSymbols = symbolIndex,
                        CurrentBatch = CurrentBatch,
                        TotalBatches = TotalBatches,
                        CurrentSymbol = symbol,
                        Message = $"处理中：{symbolIndex}/{TotalSymbols} - {symbol}"
                    });
                
                    // 获取数据库中最新的K线数据日期
                    _logger.LogInformation($"获取 {symbol} 的最新K线日期");
                    var latestDate = await _klineRepository.GetLatestKlineDateAsync(symbol);
                    _logger.LogInformation($"{symbol} 的最新K线日期: {latestDate?.ToString("yyyy-MM-dd") ?? "无"}");
                    
                    // 如果没有历史数据，从30天前开始获取
                    var startTime = latestDate?.AddDays(1) ?? DateTime.Now.AddDays(-30);
                    var endTime = DateTime.Now.Date;

                    // 如果最新数据已经是今天的，则跳过
                    if (startTime >= endTime)
                    {
                        _logger.LogInformation($"交易对 {symbol} ({symbolIndex}/{TotalSymbols}) 数据已是最新");
                        return;
                    }

                    // 检查历史数据是否存在
                    _logger.LogInformation($"检查 {symbol} 的历史数据");
                    var existingData = await _klineRepository.GetKlineDataListAsync(symbol, startTime, endTime);
                    var existingDates = existingData.Select(k => k.OpenTime.Date).ToHashSet();
                    _logger.LogInformation($"{symbol} 已有 {existingDates.Count} 天的数据");
                    
                    // 计算需要补充的日期
                    var datesToFetch = new List<DateTime>();
                    for (var date = startTime.Date; date <= endTime.Date; date = date.AddDays(1))
                    {
                        if (!existingDates.Contains(date))
                        {
                            datesToFetch.Add(date);
                        }
                    }

                    if (datesToFetch.Count == 0)
                    {
                        _logger.LogInformation($"交易对 {symbol} ({symbolIndex}/{TotalSymbols}) 所有数据已存在");
                        return;
                    }

                    // 只获取缺失的日期数据
                    _logger.LogInformation($"交易对 {symbol} ({symbolIndex}/{TotalSymbols}) 需要补充 {datesToFetch.Count} 天的数据: {string.Join(", ", datesToFetch.Select(d => d.ToString("yyyy-MM-dd")))}");

                    // 获取需要补充的数据
                    _logger.LogInformation($"开始获取 {symbol} 的K线数据");
                    var klines = await _binanceApiService.GetKlinesAsync(
                        symbol,
                        "1d",
                        datesToFetch.First(), // 只获取缺失数据的第一天
                        datesToFetch.Last().AddDays(1).AddSeconds(-1)); // 到缺失数据的最后一天

                    if (klines.Count == 0)
                    {
                        _logger.LogError($"获取 {symbol} ({symbolIndex}/{TotalSymbols}) K线数据失败，返回空列表");
                        return;
                    }

                    _logger.LogInformation($"获取到 {symbol} 的 {klines.Count} 条K线数据");

                    // 过滤出需要补充的数据
                    var klineData = klines
                        .Where(k => datesToFetch.Contains(k.OpenTime.Date))
                        .Select(k => new KlineData
                    {
                        Symbol = symbol,
                        OpenTime = k.OpenTime,
                        OpenPrice = k.Open,
                        HighPrice = k.High,
                        LowPrice = k.Low,
                        ClosePrice = k.Close,
                        Volume = k.Volume,
                        CloseTime = k.CloseTime,
                        QuoteVolume = k.QuoteVolume,
                        TradeCount = k.TradeCount,
                        TakerBuyVolume = k.TakerBuyVolume,
                        TakerBuyQuoteVolume = k.TakerBuyQuoteVolume,
                        CreatedAt = DateTime.Now
                    }).ToList();

                    // 检查集合是否有元素
                    bool hasData = klineData.Count > 0;
                    if (hasData)
                    {
                        _logger.LogInformation($"开始保存 {symbol} 的 {klineData.Count} 条K线数据");
                        var savedResult = await _klineRepository.SaveKlineDataBatchAsync(klineData);
                        _logger.LogInformation($"已保存 {symbol} ({symbolIndex}/{TotalSymbols}) 的 {klineData.Count} 条K线数据，结果: {savedResult}");
                    }
                    else
                    {
                        _logger.LogInformation($"交易对 {symbol} ({symbolIndex}/{TotalSymbols}) 没有需要补充的数据");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"处理交易对 {symbol} ({symbolIndex}/{TotalSymbols}) 时发生错误");
                }
            }, cancellationToken);
            
            tasks.Add(task);
        }
        
        try
        {
            _logger.LogInformation($"等待当前批次的所有任务完成，共 {tasks.Count} 个任务");
            await Task.WhenAll(tasks);
            _logger.LogInformation("当前批次的所有任务已完成");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("处理交易对批次已取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "等待交易对处理任务完成时发生错误");
        }
    }
    
    // 触发进度更新事件
    protected virtual void OnProgressUpdated(ProgressUpdateEventArgs e)
    {
        ProgressUpdated?.Invoke(this, e);
    }
    
    // 检查服务是否正在获取数据
    public bool IsFetching => _isFetching;

    // 添加手动触发数据更新的方法
    public async Task TriggerManualUpdateAsync()
    {
        if (_isFetching)
        {
            _logger.LogWarning("数据更新正在进行中，请等待当前更新完成");
            return;
        }

        try 
        {
            _logger.LogInformation("开始手动触发数据更新");
            _isFetching = true;
            _cancellationTokenSource = new CancellationTokenSource();
            await FetchKlineDataAsync(_cancellationTokenSource.Token);
            _isFetching = false;
            _logger.LogInformation("手动数据更新完成");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("数据更新已取消");
            _isFetching = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "手动触发数据更新失败");
            _isFetching = false;
            throw;
        }
    }

    // 添加新方法：检查今天的数据是否已经存在
    private async Task<bool> CheckTodayDataExistsAsync()
    {
        try
        {
            // 获取任意一个交易对的数据作为检查样本
            var sampleSymbols = await _klineRepository.GetSymbolsWithDataAsync();
            if (sampleSymbols == null || !sampleSymbols.Any())
            {
                _logger.LogInformation("数据库中没有交易对数据，需要首次获取");
                return false;
            }

            var today = DateTime.Now.Date;
            var sampleSymbol = sampleSymbols.First();
            
            // 检查今天的数据是否存在
            var todayData = await _klineRepository.GetKlineDataListAsync(
                sampleSymbol,
                today,
                today.AddDays(1).AddSeconds(-1));

            if (todayData != null && todayData.Any())
            {
                _logger.LogInformation($"已找到今天({today:yyyy-MM-dd})的K线数据，样本交易对: {sampleSymbol}");
                return true;
            }

            _logger.LogInformation($"未找到今天({today:yyyy-MM-dd})的K线数据，需要更新");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查今天数据是否存在时发生错误");
            // 如果检查过程出错，返回false以确保数据会被更新
            return false;
        }
    }
}

// 进度更新事件参数
public class ProgressUpdateEventArgs : EventArgs
{
    public int TotalSymbols { get; set; }
    public int ProcessedSymbols { get; set; }
    public int CurrentBatch { get; set; }
    public int TotalBatches { get; set; }
    public string CurrentSymbol { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
} 