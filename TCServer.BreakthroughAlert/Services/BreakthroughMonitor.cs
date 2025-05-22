using Serilog;
using TCServer.BreakthroughAlert.Models;
using TCServer.BreakthroughAlert.Services.Interfaces;
using TCServer.Common.Models;
using TCServer.Core.Services;
using System.Linq;

namespace TCServer.BreakthroughAlert.Services;

public class BreakthroughMonitor : IBreakthroughMonitor
{
    private readonly IFileStorageService _storageService;
    private readonly IAlertMessageService _alertService;
    private readonly BinanceApiService _binanceApiService;
    private readonly ILogger _logger;
    private BreakthroughConfig _config;
    private MonitorStatus _status = MonitorStatus.Stopped;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly object _lockObj = new();
    private readonly Dictionary<string, decimal> _lastPrices = new();
    private readonly Dictionary<string, Dictionary<int, decimal>> _historicalHighs = new();
    private readonly Dictionary<string, Dictionary<int, decimal>> _historicalLows = new();
    private readonly Queue<AlertMessage> _messageQueue = new();
    private DateTime _lastSummaryTime = DateTime.Now;
    private bool _isProcessing = false;

    public event EventHandler<BreakthroughEvent>? OnBreakthrough;
    public event EventHandler<MonitorStatus>? OnStatusChanged;
    public event EventHandler<Exception>? OnError;

    public BreakthroughMonitor(
        IFileStorageService storageService,
        IAlertMessageService alertService,
        BinanceApiService binanceApiService,
        ILogger logger)
    {
        _storageService = storageService;
        _alertService = alertService;
        _binanceApiService = binanceApiService;
        _logger = logger;
        _config = new BreakthroughConfig();
    }

    public async Task StartMonitoringAsync()
    {
        if (_status == MonitorStatus.Running)
            return;

        try
        {
            _config = await _storageService.LoadConfigAsync<BreakthroughConfig>("breakthrough_config");
            
            if (_config.SummaryWindowSeconds <= 0)
            {
                throw new InvalidOperationException("推送时间间隔必须大于0秒");
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _status = MonitorStatus.Running;
            _lastSummaryTime = DateTime.Now;
            OnStatusChanged?.Invoke(this, _status);

            _ = Task.Run(async () => await MonitorAsync(_cancellationTokenSource.Token));
            _ = Task.Run(async () => await ProcessMessageQueueAsync(_cancellationTokenSource.Token));

            _logger.Information("突破监控服务已启动");
        }
        catch (Exception ex)
        {
            _status = MonitorStatus.Error;
            OnStatusChanged?.Invoke(this, _status);
            OnError?.Invoke(this, ex);
            _logger.Error(ex, "启动突破监控服务失败");
            throw;
        }
    }

    public async Task StopMonitoringAsync()
    {
        if (_status != MonitorStatus.Running)
            return;

        try
        {
            await Task.Run(() => _cancellationTokenSource?.Cancel());
            _status = MonitorStatus.Stopped;
            OnStatusChanged?.Invoke(this, _status);
            _logger.Information("突破监控服务已停止");
        }
        catch (Exception ex)
        {
            _status = MonitorStatus.Error;
            OnStatusChanged?.Invoke(this, _status);
            OnError?.Invoke(this, ex);
            _logger.Error(ex, "停止突破监控服务失败");
        }
    }

    public async Task UpdateConfigAsync(BreakthroughConfig config)
    {
        lock (_lockObj)
        {
            _config = config;
        }
        await _storageService.SaveConfigAsync("breakthrough_config", config);
        _logger.Information("更新突破监控配置成功");
    }

    public MonitorStatus GetStatus() => _status;

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_isProcessing)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                _isProcessing = true;
                var marketData = await GetMarketDataAsync();
                if (marketData != null && marketData.Any())
                {
                    await ProcessMarketDataAsync(marketData);
                }
                _isProcessing = false;

                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _isProcessing = false;
                _logger.Error(ex, "处理市场数据时发生错误");
                OnError?.Invoke(this, ex);
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    private async Task ProcessMarketDataAsync(IEnumerable<TickData> marketData)
    {
        var upThresholdStats = new Dictionary<decimal, int>();
        var downThresholdStats = new Dictionary<decimal, int>();
        var newHighStats = new Dictionary<int, int>();
        var newLowStats = new Dictionary<int, int>();
        var processedCount = 0;
        var skippedCount = 0;
        var scanTime = DateTime.Now;

        _logger.Information($"开始处理 {marketData.Count()} 个交易对的数据");

        // 获取当前启用的阈值配置（去重）
        var enabledUpThresholds = _config.UpThresholds
            .Where(t => t.IsEnabled)
            .Select(t => t.Value)
            .Distinct()
            .OrderByDescending(v => v)
            .ToList();
        var enabledDownThresholds = _config.DownThresholds
            .Where(t => t.IsEnabled)
            .Select(t => t.Value)
            .Distinct()
            .OrderBy(v => v)
            .ToList();
        var enabledHighDays = _config.NewHighConfigs
            .Where(c => c.IsEnabled)
            .Select(c => c.Days)
            .Distinct()
            .OrderBy(d => d)
            .ToList();
        var enabledLowDays = _config.NewLowConfigs
            .Where(c => c.IsEnabled)
            .Select(c => c.Days)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        _logger.Information($"当前启用的检测配置：涨幅阈值 {string.Join(", ", enabledUpThresholds)}%, " +
                          $"跌幅阈值 {string.Join(", ", enabledDownThresholds)}%, " +
                          $"新高天数 {string.Join(", ", enabledHighDays)}日, " +
                          $"新低天数 {string.Join(", ", enabledLowDays)}日");

        foreach (var data in marketData)
        {
            try
            {
                // 检查是否有历史价格数据
                if (!_lastPrices.TryGetValue(data.Symbol, out var lastPrice))
                {
                    _logger.Debug($"交易对 {data.Symbol} 首次获取数据，当前价格: {data.LastPrice}");
                    _lastPrices[data.Symbol] = data.LastPrice;
                    skippedCount++;
                    continue;
                }

                // 计算价格变化
                var changePercent = (data.LastPrice - lastPrice) / lastPrice * 100;
                if (Math.Abs(changePercent) >= 0.1m)  // 只记录变化超过0.1%的价格
                {
                    _logger.Information($"交易对 {data.Symbol} 价格变化: {changePercent:F2}%, 当前价: {data.LastPrice}, 上次价: {lastPrice}");
                }

                // 更新历史数据
                UpdateHistoricalData(data);
                processedCount++;

                // 检查涨幅突破
                var upThresholds = await CheckUpThresholdAsync(data);
                foreach (var threshold in upThresholds)
                {
                    if (!upThresholdStats.ContainsKey(threshold))
                        upThresholdStats[threshold] = 0;
                    upThresholdStats[threshold]++;
                }

                // 检查跌幅突破
                var downThresholds = await CheckDownThresholdAsync(data);
                foreach (var threshold in downThresholds)
                {
                    if (!downThresholdStats.ContainsKey(threshold))
                        downThresholdStats[threshold] = 0;
                    downThresholdStats[threshold]++;
                }

                // 检查新高突破
                var newHighs = await CheckNewHighAsync(data);
                foreach (var days in newHighs)
                {
                    if (!newHighStats.ContainsKey(days))
                        newHighStats[days] = 0;
                    newHighStats[days]++;
                }

                // 检查新低突破
                var newLows = await CheckNewLowAsync(data);
                foreach (var days in newLows)
                {
                    if (!newLowStats.ContainsKey(days))
                        newLowStats[days] = 0;
                    newLowStats[days]++;
                }

                // 更新最新价格
                _lastPrices[data.Symbol] = data.LastPrice;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"处理交易对 {data.Symbol} 数据时发生错误");
            }
        }

        // 输出本轮扫描汇总信息
        var scanDuration = DateTime.Now - scanTime;
        _logger.Information($"本轮扫描完成，耗时: {scanDuration.TotalMilliseconds:F0}ms");
        _logger.Information($"数据处理统计: 共处理 {processedCount} 个交易对, 跳过 {skippedCount} 个首次数据");

        // 输出突破统计
        if (upThresholdStats.Any())
        {
            _logger.Information("涨幅突破汇总：");
            foreach (var stat in upThresholdStats.OrderByDescending(x => x.Key))
            {
                _logger.Information($"突破 {stat.Key}% 的有 {stat.Value} 个交易对");
            }
        }
        else if (enabledUpThresholds.Any())
        {
            _logger.Information("本轮扫描未发现涨幅突破");
        }

        if (downThresholdStats.Any())
        {
            _logger.Information("跌幅突破汇总：");
            foreach (var stat in downThresholdStats.OrderBy(x => x.Key))
            {
                _logger.Information($"突破 {stat.Key}% 的有 {stat.Value} 个交易对");
            }
        }
        else if (enabledDownThresholds.Any())
        {
            _logger.Information("本轮扫描未发现跌幅突破");
        }

        if (newHighStats.Any())
        {
            _logger.Information("新高突破汇总：");
            foreach (var stat in newHighStats.OrderBy(x => x.Key))
            {
                _logger.Information($"突破 {stat.Key} 日新高的有 {stat.Value} 个交易对");
            }
        }
        else if (enabledHighDays.Any())
        {
            _logger.Information("本轮扫描未发现新高突破");
        }

        if (newLowStats.Any())
        {
            _logger.Information("新低突破汇总：");
            foreach (var stat in newLowStats.OrderBy(x => x.Key))
            {
                _logger.Information($"突破 {stat.Key} 日新低的有 {stat.Value} 个交易对");
            }
        }
        else if (enabledLowDays.Any())
        {
            _logger.Information("本轮扫描未发现新低突破");
        }

        // 计算距离下次推送的时间
        var nextPushTime = _lastSummaryTime.AddSeconds(_config.SummaryWindowSeconds);
        var timeToNextPush = nextPushTime - DateTime.Now;
        if (timeToNextPush.TotalSeconds > 0)
        {
            _logger.Information($"距离下次推送时间还有 {timeToNextPush.TotalSeconds:F0} 秒");
        }
    }

    private void UpdateHistoricalData(TickData data)
    {
        if (!_historicalHighs.ContainsKey(data.Symbol))
        {
            _historicalHighs[data.Symbol] = new Dictionary<int, decimal>();
            _historicalLows[data.Symbol] = new Dictionary<int, decimal>();
            _logger.Debug($"交易对 {data.Symbol} 初始化历史数据记录");
        }

        var highs = _historicalHighs[data.Symbol];
        var lows = _historicalLows[data.Symbol];

        // 分别处理新高和新低配置
        foreach (var config in _config.NewHighConfigs)
        {
            if (!config.IsEnabled)
                continue;

            if (!highs.ContainsKey(config.Days))
            {
                highs[config.Days] = data.LastPrice;
                _logger.Debug($"交易对 {data.Symbol} 初始化 {config.Days} 日新高记录: {data.LastPrice}");
            }
            else if (data.LastPrice > highs[config.Days])
            {
                _logger.Debug($"交易对 {data.Symbol} 更新 {config.Days} 日新高: {highs[config.Days]} -> {data.LastPrice}");
                highs[config.Days] = data.LastPrice;
            }
        }

        foreach (var config in _config.NewLowConfigs)
        {
            if (!config.IsEnabled)
                continue;

            if (!lows.ContainsKey(config.Days))
            {
                lows[config.Days] = data.LastPrice;
                _logger.Debug($"交易对 {data.Symbol} 初始化 {config.Days} 日新低记录: {data.LastPrice}");
            }
            else if (data.LastPrice < lows[config.Days])
            {
                _logger.Debug($"交易对 {data.Symbol} 更新 {config.Days} 日新低: {lows[config.Days]} -> {data.LastPrice}");
                lows[config.Days] = data.LastPrice;
            }
        }
    }

    private async Task<List<decimal>> CheckUpThresholdAsync(TickData data)
    {
        var triggeredThresholds = new List<decimal>();
        if (!_lastPrices.TryGetValue(data.Symbol, out var lastPrice))
            return triggeredThresholds;

        var changePercent = (data.LastPrice - lastPrice) / lastPrice * 100;
        _logger.Debug($"扫描 {data.Symbol} 涨幅: 当前价 {data.LastPrice}, 上次价 {lastPrice}, 变化 {changePercent:F2}%");

        var enabledThresholds = _config.UpThresholds.Where(t => t.IsEnabled).ToList();
        if (!enabledThresholds.Any())
        {
            _logger.Debug($"交易对 {data.Symbol} 没有启用涨幅阈值");
            return triggeredThresholds;
        }

        _logger.Debug($"交易对 {data.Symbol} 涨幅阈值检查: {string.Join(", ", enabledThresholds.Select(t => $"{t.Value}%"))}");

        foreach (var threshold in enabledThresholds)
        {
            if (changePercent >= threshold.Value)
            {
                _logger.Information($"交易对 {data.Symbol} 触发涨幅突破: 当前涨幅 {changePercent:F2}% >= 阈值 {threshold.Value}%");
                var @event = new BreakthroughEvent
                {
                    Symbol = data.Symbol,
                    CurrentPrice = data.LastPrice,
                    ThresholdValue = threshold.Value,
                    Type = BreakthroughType.UpThreshold,
                    ChangePercent = changePercent,
                    Volume = data.Volume,
                    Description = threshold.Description
                };

                OnBreakthrough?.Invoke(this, @event);
                await QueueAlertMessageAsync(@event);
                triggeredThresholds.Add(threshold.Value);
            }
            else
            {
                _logger.Debug($"交易对 {data.Symbol} 涨幅未达阈值: 当前涨幅 {changePercent:F2}% < 阈值 {threshold.Value}%");
            }
        }
        return triggeredThresholds;
    }

    private async Task<List<decimal>> CheckDownThresholdAsync(TickData data)
    {
        var triggeredThresholds = new List<decimal>();
        if (!_lastPrices.TryGetValue(data.Symbol, out var lastPrice))
            return triggeredThresholds;

        var changePercent = (data.LastPrice - lastPrice) / lastPrice * 100;
        _logger.Debug($"扫描 {data.Symbol} 跌幅: 当前价 {data.LastPrice}, 上次价 {lastPrice}, 变化 {changePercent:F2}%");

        var enabledThresholds = _config.DownThresholds.Where(t => t.IsEnabled).ToList();
        if (!enabledThresholds.Any())
        {
            _logger.Debug($"交易对 {data.Symbol} 没有启用跌幅阈值");
            return triggeredThresholds;
        }

        _logger.Debug($"交易对 {data.Symbol} 跌幅阈值检查: {string.Join(", ", enabledThresholds.Select(t => $"{t.Value}%"))}");

        foreach (var threshold in enabledThresholds)
        {
            if (changePercent <= threshold.Value)
            {
                _logger.Information($"交易对 {data.Symbol} 触发跌幅突破: 当前跌幅 {changePercent:F2}% <= 阈值 {threshold.Value}%");
                var @event = new BreakthroughEvent
                {
                    Symbol = data.Symbol,
                    CurrentPrice = data.LastPrice,
                    ThresholdValue = threshold.Value,
                    Type = BreakthroughType.DownThreshold,
                    ChangePercent = changePercent,
                    Volume = data.Volume,
                    Description = threshold.Description
                };

                OnBreakthrough?.Invoke(this, @event);
                await QueueAlertMessageAsync(@event);
                triggeredThresholds.Add(threshold.Value);
            }
            else
            {
                _logger.Debug($"交易对 {data.Symbol} 跌幅未达阈值: 当前跌幅 {changePercent:F2}% > 阈值 {threshold.Value}%");
            }
        }
        return triggeredThresholds;
    }

    private async Task<List<int>> CheckNewHighAsync(TickData data)
    {
        var triggeredDays = new List<int>();
        if (!_historicalHighs.TryGetValue(data.Symbol, out var highs))
            return triggeredDays;

        var enabledConfigs = _config.NewHighConfigs.Where(c => c.IsEnabled).ToList();
        if (!enabledConfigs.Any())
        {
            _logger.Debug($"交易对 {data.Symbol} 没有启用新高检测");
            return triggeredDays;
        }

        _logger.Debug($"交易对 {data.Symbol} 新高检测: 当前价 {data.LastPrice}, 检测天数 {string.Join(", ", enabledConfigs.Select(c => $"{c.Days}日"))}");

        foreach (var config in enabledConfigs)
        {
            if (highs.TryGetValue(config.Days, out var highPrice))
            {
                if (data.LastPrice > highPrice)
                {
                    var changePercent = (data.LastPrice - highPrice) / highPrice * 100;
                    _logger.Information($"交易对 {data.Symbol} 触发 {config.Days} 日新高: 当前价 {data.LastPrice} > 历史最高 {highPrice}, 涨幅 {changePercent:F2}%");
                    var @event = new BreakthroughEvent
                    {
                        Symbol = data.Symbol,
                        CurrentPrice = data.LastPrice,
                        ThresholdValue = highPrice,
                        Type = BreakthroughType.NewHigh,
                        ChangePercent = changePercent,
                        Volume = data.Volume,
                        Description = config.Description
                    };

                    OnBreakthrough?.Invoke(this, @event);
                    await QueueAlertMessageAsync(@event);
                    triggeredDays.Add(config.Days);
                }
                else
                {
                    _logger.Debug($"交易对 {data.Symbol} {config.Days} 日新高未突破: 当前价 {data.LastPrice} <= 历史最高 {highPrice}");
                }
            }
        }
        return triggeredDays;
    }

    private async Task<List<int>> CheckNewLowAsync(TickData data)
    {
        var triggeredDays = new List<int>();
        if (!_historicalLows.TryGetValue(data.Symbol, out var lows))
            return triggeredDays;

        var enabledConfigs = _config.NewLowConfigs.Where(c => c.IsEnabled).ToList();
        if (!enabledConfigs.Any())
        {
            _logger.Debug($"交易对 {data.Symbol} 没有启用新低检测");
            return triggeredDays;
        }

        _logger.Debug($"交易对 {data.Symbol} 新低检测: 当前价 {data.LastPrice}, 检测天数 {string.Join(", ", enabledConfigs.Select(c => $"{c.Days}日"))}");

        foreach (var config in enabledConfigs)
        {
            if (lows.TryGetValue(config.Days, out var lowPrice))
            {
                if (data.LastPrice < lowPrice)
                {
                    var changePercent = (data.LastPrice - lowPrice) / lowPrice * 100;
                    _logger.Information($"交易对 {data.Symbol} 触发 {config.Days} 日新低: 当前价 {data.LastPrice} < 历史最低 {lowPrice}, 跌幅 {changePercent:F2}%");
                    var @event = new BreakthroughEvent
                    {
                        Symbol = data.Symbol,
                        CurrentPrice = data.LastPrice,
                        ThresholdValue = lowPrice,
                        Type = BreakthroughType.NewLow,
                        ChangePercent = changePercent,
                        Volume = data.Volume,
                        Description = config.Description
                    };

                    OnBreakthrough?.Invoke(this, @event);
                    await QueueAlertMessageAsync(@event);
                    triggeredDays.Add(config.Days);
                }
                else
                {
                    _logger.Debug($"交易对 {data.Symbol} {config.Days} 日新低未突破: 当前价 {data.LastPrice} >= 历史最低 {lowPrice}");
                }
            }
        }
        return triggeredDays;
    }

    private async Task QueueAlertMessageAsync(BreakthroughEvent @event)
    {
        var message = new AlertMessage
        {
            Symbol = @event.Symbol,
            Type = @event.Type switch
            {
                BreakthroughType.UpThreshold => AlertType.UpAlert,
                BreakthroughType.DownThreshold => AlertType.DownAlert,
                BreakthroughType.NewHigh => AlertType.HighAlert,
                BreakthroughType.NewLow => AlertType.LowAlert,
                _ => AlertType.UpAlert
            },
            CurrentPrice = @event.CurrentPrice,
            ChangePercent = @event.ChangePercent,
            Volume = @event.Volume,
            Description = @event.Description,
            AlertTime = @event.EventTime
        };

        await Task.Run(() =>
        {
            lock (_messageQueue)
            {
                _messageQueue.Enqueue(message);
            }
        });
    }

    private async Task ProcessMessageQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var timeSinceLastSummary = (now - _lastSummaryTime).TotalSeconds;

                if (timeSinceLastSummary >= _config.SummaryWindowSeconds)
                {
                    var messages = new List<AlertMessage>();
                    var hasMessages = false;

                    await Task.Run(() =>
                    {
                        lock (_messageQueue)
                        {
                            hasMessages = _messageQueue.Count > 0;
                            while (_messageQueue.Count > 0)
                            {
                                messages.Add(_messageQueue.Dequeue());
                            }
                        }
                    });

                    if (hasMessages)
                    {
                        try
                        {
                            await _alertService.SendBatchAlertsAsync(messages);
                            _lastSummaryTime = now;
                            _logger.Information($"成功推送 {messages.Count} 条消息");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "批量发送消息失败");
                            await Task.Run(() =>
                            {
                                lock (_messageQueue)
                                {
                                    foreach (var message in messages)
                                    {
                                        _messageQueue.Enqueue(message);
                                    }
                                }
                            });
                        }
                    }
                }

                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "处理消息队列时发生错误");
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    private async Task<IEnumerable<TickData>> GetMarketDataAsync()
    {
        try
        {
            var tickers = await _binanceApiService.Get24hrTickerAsync();
            if (tickers == null || !tickers.Any())
            {
                _logger.Warning("获取24小时行情数据失败或返回空列表");
                return Enumerable.Empty<TickData>();
            }

            var result = tickers.Select(t => new TickData
            {
                Symbol = t.Symbol,
                LastPrice = t.LastPrice,
                Volume = t.Volume,
                OpenPrice = t.OpenPrice,
                HighPrice = t.HighPrice,
                LowPrice = t.LowPrice,
                PriceChangePercent = t.PriceChangePercent,
                QuoteVolume = t.QuoteVolume,
                Timestamp = DateTime.Now
            }).ToList();

            if (!result.Any())
            {
                _logger.Warning("转换行情数据后为空");
            }
            else
            {
                _logger.Debug($"获取到 {result.Count} 个交易对的行情数据");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "获取市场数据时发生错误");
            return Enumerable.Empty<TickData>();
        }
    }
} 