using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TCServer.Common.Interfaces;
using TCServer.Common.Models;
using TCServer.Core.Services;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.IO;
using Newtonsoft.Json;
using System.Net.Http;
using System.Text;
using TCServer.Models;

namespace TCServer
{
    public partial class RankingWindow : Window
    {
        private readonly IDailyRankingRepository _rankingRepository;
        private readonly RankingService _rankingService;
        private readonly BinanceApiService _binanceApiService;
        private readonly ILogger<RankingWindow> _logger;
        private CancellationTokenSource? _realtimeCts;
        private readonly ObservableCollection<HistoryRankingViewModel> _historyRankings;
        private readonly ObservableCollection<HistoryGainerDayViewModel> _historyTopGainers;
        private readonly ObservableCollection<HistoryLoserDayViewModel> _historyTopLosers;
        private readonly ObservableCollection<RealtimeRankingViewModel> _realtimeTopGainers;
        private readonly ObservableCollection<RealtimeRankingViewModel> _realtimeTopLosers;
        private DateTime _lastUpdateTime;
        private DateTime _nextUpdateTime;
        private bool _isRealtimeRunning;
        
        // 突破提醒相关字段
        private BreakthroughSettings _breakthroughSettings;
        private readonly string _settingsFilePath = "settings.json";
        private readonly HttpClient _httpClient;
        
        // 突破事件收集字段
        private readonly List<BreakthroughEvent> _pendingUptrends = new List<BreakthroughEvent>();
        private readonly List<BreakthroughEvent> _pendingDowntrends = new List<BreakthroughEvent>();
        private readonly List<BreakthroughEvent> _pendingHighBreaks = new List<BreakthroughEvent>();
        private readonly List<BreakthroughEvent> _pendingLowBreaks = new List<BreakthroughEvent>();
        private Timer _notificationTimer;
        private readonly object _pendingEventsLock = new object();
        private const int NOTIFICATION_INTERVAL_MS = 60000; // 每分钟汇总发送一次

        // 添加新高/新低数据缓存
        private readonly Dictionary<string, Dictionary<int, (decimal High, decimal Low)>> _highLowCache = new();
        private readonly object _highLowCacheLock = new object();
        private bool _isHighLowCacheInitialized = false;

        public RankingWindow()
        {
            InitializeComponent();
            
            // 获取服务
            var host = ((App)Application.Current).Host;
            _rankingRepository = host.Services.GetRequiredService<IDailyRankingRepository>();
            _rankingService = host.Services.GetRequiredService<RankingService>();
            _binanceApiService = host.Services.GetRequiredService<BinanceApiService>();
            _logger = host.Services.GetRequiredService<ILogger<RankingWindow>>();
            
            // 订阅排名服务的日志事件
            if (_rankingService != null)
            {
                _rankingService.LogUpdated += RankingService_LogUpdated;
            }
            
            // 初始化集合
            _historyRankings = new ObservableCollection<HistoryRankingViewModel>();
            _historyTopGainers = new ObservableCollection<HistoryGainerDayViewModel>();
            _historyTopLosers = new ObservableCollection<HistoryLoserDayViewModel>();
            _realtimeTopGainers = new ObservableCollection<RealtimeRankingViewModel>();
            _realtimeTopLosers = new ObservableCollection<RealtimeRankingViewModel>();
            
            // 初始化HTTP客户端
            _httpClient = new HttpClient();
            
            // 加载突破提醒设置
            LoadBreakthroughSettings();
            
            // 初始化通知定时器
            _notificationTimer = new Timer(SendPendingNotifications, null, NOTIFICATION_INTERVAL_MS, NOTIFICATION_INTERVAL_MS);
            
            // 绑定数据源
            lvHistoryTopGainers.ItemsSource = _historyTopGainers;
            lvHistoryTopLosers.ItemsSource = _historyTopLosers;
            lvRealtimeTopGainers.ItemsSource = _realtimeTopGainers;
            lvRealtimeTopLosers.ItemsSource = _realtimeTopLosers;
            
            // 清空日志
            txtLog.Clear();
            
            // 默认选择今天日期
            datePicker.SelectedDate = DateTime.Now.Date;
            
            // 初始化新高/新低数据缓存
            InitializeHighLowCacheAsync();
        }
        
        // 处理排名服务日志事件
        private void RankingService_LogUpdated(object? sender, RankingLogEventArgs e)
        {
            Dispatcher.Invoke(() => AppendLog(e.ToString()));
        }

        private async Task StartRealtimeRankingAsync()
        {
            if (_isRealtimeRunning)
            {
                AppendLog("实时排名已经在运行中");
                return;
            }

            try
            {
                _isRealtimeRunning = true;
                _realtimeCts = new CancellationTokenSource();
                
                // 清空实时排名数据
                _realtimeTopGainers.Clear();
                _realtimeTopLosers.Clear();
                
                AppendLog("开始实时排名计算...");
                
                // 启动定时更新任务
                _ = Task.Run(async () =>
                {
                    while (!_realtimeCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            // 获取所有交易对的最新K线数据
                            var symbols = await _binanceApiService.GetPerpetualSymbolsAsync();
                            var rankings = new List<RealtimeRankingViewModel>();
                            
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
                                            
                                            rankings.Add(new RealtimeRankingViewModel
                                            {
                                                Symbol = symbol,
                                                LastPrice = latestKline.Close,
                                                OpenPrice = previousKline.Open,
                                                Percentage = percentage / 100, // 转换为小数
                                                QuoteVolume = latestKline.QuoteVolume
                                            });
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AppendLog($"获取 {symbol} 数据时出错: {ex.Message}");
                                }
                                
                                // 添加延迟以避免请求过于频繁
                                await Task.Delay(100, _realtimeCts.Token);
                            }
                            
                            // 更新UI
                            Dispatcher.Invoke(() =>
                            {
                                UpdateRealtimeRankings(rankings);
                                _lastUpdateTime = DateTime.Now;
                                _nextUpdateTime = _lastUpdateTime.AddSeconds(5);
                                UpdateStatusTexts();
                            });
                            
                            // 等待到下一次更新
                            await Task.Delay(5000, _realtimeCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"实时排名更新出错: {ex.Message}");
                            await Task.Delay(5000, _realtimeCts.Token);
                        }
                    }
                }, _realtimeCts.Token);
                
                btnStartRealtime.IsEnabled = false;
                btnStopRealtime.IsEnabled = true;
                AppendLog("实时排名已启动");
            }
            catch (Exception ex)
            {
                _isRealtimeRunning = false;
                AppendLog($"启动实时排名失败: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
            }
        }

        private void UpdateRealtimeRankings(List<RealtimeRankingViewModel> rankings)
        {
            try
            {
                // 清空现有数据
                _realtimeTopGainers.Clear();
                _realtimeTopLosers.Clear();
                
                if (rankings == null || !rankings.Any())
                {
                    return;
                }
                
                // 按涨幅排序
                var sortedRankings = rankings
                    .OrderByDescending(r => r.Percentage)
                    .ToList();
                
                // 更新涨幅排名
                for (int i = 0; i < Math.Min(10, sortedRankings.Count); i++)
                {
                    var ranking = sortedRankings[i];
                    ranking.Rank = i + 1;
                    _realtimeTopGainers.Add(ranking);
                }
                
                // 更新跌幅排名
                for (int i = 0; i < Math.Min(10, sortedRankings.Count); i++)
                {
                    var ranking = sortedRankings[sortedRankings.Count - 1 - i];
                    ranking.Rank = i + 1;
                    _realtimeTopLosers.Add(ranking);
                }
                
                // 检查突破提醒
                CheckAndSendBreakthroughAlerts(sortedRankings);
            }
            catch (Exception ex)
            {
                AppendLog($"更新实时排名时出错: {ex.Message}");
            }
        }

        private async void LoadHistoryRankingsAsync()
        {
            try
            {
                if (_rankingRepository == null || _rankingService == null) return;

                // 清空历史排名数据
                _historyTopGainers.Clear();
                _historyTopLosers.Clear();
                
                var today = DateTime.Now.Date;
                var endDate = today.AddDays(-1);  // 昨天
                var startDate = endDate.AddDays(-28);  // 29天前
                
                AppendLog($"加载历史排名数据 ({startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd})");
                
                // 获取最近30天的排名数据
                var rankings = await _rankingRepository.GetRecentRankingsAsync(30);
                if (rankings == null || !rankings.Any())
                {
                    AppendLog("未找到历史排名数据，开始自动计算历史排名...");
                    
                    // 自动开始计算历史排名
                    await _rankingService.CheckAndCalculateMissingRankingsAsync();
                    
                    // 重新加载排名数据
                    rankings = await _rankingRepository.GetRecentRankingsAsync(30);
                    if (rankings == null || !rankings.Any())
                    {
                        AppendLog("计算后仍未找到历史排名数据，可能存在数据问题");
                        return;
                    }
                    
                    AppendLog("历史排名计算完成，数据已加载");
                }

                // 过滤出指定日期范围内的数据
                var validRankings = rankings
                    .Where(r => r.Date >= startDate && r.Date <= endDate)
                    .OrderByDescending(r => r.Date)  // 按日期降序排列（最新的排在前面）
                    .ToList();

                if (!validRankings.Any())
                {
                    AppendLog(@"指定日期范围内仍未找到历史排名数据，请检查数据和计算逻辑");
                    return;
                }

                AppendLog($"已加载 {validRankings.Count} 条历史排名数据");
                
                // 处理每一天的排名数据
                foreach (var dailyRanking in validRankings)
                {
                    // 解析涨幅排名
                    var gainers = ParseRankingList(dailyRanking.TopGainers);
                    var gainerViewModel = new HistoryGainerDayViewModel
                    {
                        Date = dailyRanking.Date
                    };
                    
                    // 填充涨幅前十名
                    FillRankingViewModel(gainerViewModel, gainers);
                    _historyTopGainers.Add(gainerViewModel);
                    
                    // 解析跌幅排名
                    var losers = ParseRankingList(dailyRanking.TopLosers);
                    var loserViewModel = new HistoryLoserDayViewModel
                    {
                        Date = dailyRanking.Date
                    };
                    
                    // 填充跌幅前十名
                    FillRankingViewModel(loserViewModel, losers);
                    _historyTopLosers.Add(loserViewModel);
                }
                
                AppendLog($"已显示 {_historyTopGainers.Count} 天的涨幅排名和 {_historyTopLosers.Count} 天的跌幅排名");
            }
            catch (Exception ex)
            {
                AppendLog($"加载历史排名数据失败：{ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常：{ex.InnerException.Message}");
                }
            }
        }

        // 解析排名字符串为排名项列表
        private List<RankingItem> ParseRankingList(string rankingData)
        {
            if (string.IsNullOrEmpty(rankingData)) return new List<RankingItem>();
            
            try
            {
                return rankingData
                    .Split('|')
                    .Select(item => RankingItem.Parse(item))
                    .Where(item => item != null)
                    .OrderBy(item => item.Rank)
                    .Take(10)
                    .ToList();
            }
            catch
            {
                return new List<RankingItem>();
            }
        }

        // 填充排名数据到视图模型
        private void FillRankingViewModel<T>(T viewModel, List<RankingItem> rankings)
        {
            var properties = typeof(T).GetProperties();
            
            for (int i = 0; i < Math.Min(rankings.Count, 10); i++)
            {
                var ranking = rankings[i];
                var propertyName = $"Rank{i + 1}";
                var property = properties.FirstOrDefault(p => p.Name == propertyName);
                
                if (property != null)
                {
                    // 去掉USDT和USDC后缀
                    string symbol = ranking.Symbol;
                    if (symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
                    {
                        symbol = symbol.Substring(0, symbol.Length - 4);
                    }
                    else if (symbol.EndsWith("USDC", StringComparison.OrdinalIgnoreCase))
                    {
                        symbol = symbol.Substring(0, symbol.Length - 4);
                    }
                    
                    // 格式：Symbol|涨跌幅百分比
                    string value = $"{symbol}|{ranking.Percentage:P2}";
                    property.SetValue(viewModel, value);
                }
            }
        }

        private void datePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (datePicker.SelectedDate.HasValue)
            {
                // 清空日志
                txtLog.Clear();
                
                // 如果选择了今天，显示提示信息
                if (datePicker.SelectedDate.Value.Date == DateTime.Now.Date)
                {
                    AppendLog(@"提示：已选择今天，可以点击""启动实时排名""按钮开始实时更新");
                }
                else
                {
                    // 如果选择了其他日期，且实时排名正在运行，则停止
                    if (_isRealtimeRunning)
                    {
                        btnStartRealtime_Click(btnStartRealtime, new RoutedEventArgs());
                    }
                    AppendLog($@"提示：已选择 {datePicker.SelectedDate.Value:yyyy-MM-dd}，可以点击""计算历史排名""按钮计算该日排名");
                }
            }
        }

        private async void btnStartRealtime_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnStartRealtime.IsEnabled = false;
                btnStopRealtime.IsEnabled = true;
                
                // 启动实时排名
                await StartRealtimeRankingAsync();
            }
            catch (Exception ex)
            {
                // 处理StartRealtimeRankingAsync中的异常
                AppendLog($"启动实时排名失败: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
                
                // 恢复按钮状态
                btnStartRealtime.IsEnabled = true;
                btnStopRealtime.IsEnabled = false;
            }
        }

        private async void btnStopRealtime_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnStartRealtime.IsEnabled = false;
                btnStopRealtime.IsEnabled = false;
                
                // 停止实时排名
                await StopRealtimeRankingAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"停止实时排名失败: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
            }
            finally
            {
                btnStartRealtime.IsEnabled = true;
                btnStopRealtime.IsEnabled = false;
            }
        }

        private async Task StopRealtimeRankingAsync()
        {
            if (!_isRealtimeRunning)
            {
                return;
            }

            try
            {
                _isRealtimeRunning = false;
                _realtimeCts?.Cancel();
                
                // 清空实时排名数据
                _realtimeTopGainers.Clear();
                _realtimeTopLosers.Clear();
                
                // 更新UI状态
                btnStartRealtime.IsEnabled = true;
                btnStopRealtime.IsEnabled = false;
                txtStatus.Text = "实时排名已停止";
                
                AppendLog("实时排名已停止");
            }
            catch (Exception ex)
            {
                AppendLog($"停止实时排名时出错: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
            }
        }

        private void UpdateStatusTexts()
        {
            txtLastUpdateTime.Text = $"最后更新时间：{_lastUpdateTime:HH:mm:ss}";
            txtNextUpdateTime.Text = $"下次更新时间：{_nextUpdateTime:HH:mm:ss}";
        }

        private async void btnCalculate_Click(object sender, RoutedEventArgs e)
        {
            if (_rankingService == null)
            {
                AppendLog("错误：无法获取排名计算服务");
                return;
            }

            try
            {
                btnCalculate.IsEnabled = false;
                
                txtLog.Clear();
                AppendLog("开始计算历史排名数据...");
                
                // 不再计算单个日期，而是调用批量计算方法
                bool result = await _rankingService.CheckAndCalculateMissingRankingsAsync();
                
                if (result)
                {
                    AppendLog("成功：历史排名数据计算完成");
                    // 重新加载历史排名数据展示
                    LoadHistoryRankingsAsync();
                }
                else
                {
                    AppendLog("提示：没有需要计算的历史排名数据，或计算过程中出现错误");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"错误：计算历史排名失败 - {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常：{ex.InnerException.Message}");
                }
            }
            finally
            {
                btnCalculate.IsEnabled = true;
            }
        }
        
        // 添加状态更新方法
        private void UpdateStatus(string message, bool isError = false)
        {
            Dispatcher.Invoke(() =>
            {
                txtStatus.Text = message;
                txtStatus.Foreground = isError ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Blue;
            });
        }
        
        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                // 取消订阅事件
                if (_rankingService != null)
                {
                    _rankingService.LogUpdated -= RankingService_LogUpdated;
                }
                
                // 异步停止实时排名
                if (_isRealtimeRunning)
                {
                    try
                    {
                        await StopRealtimeRankingAsync();
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"关闭窗口时停止实时排名出错: {ex.Message}");
                    }
                }
                
                // 清理HTTP客户端资源
                _httpClient?.Dispose();
                
                // 清理通知定时器
                _notificationTimer?.Dispose();
                _notificationTimer = null;
                
                // 等待一小段时间确保清理完成
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "关闭窗口时发生错误");
            }
        }

        // 加载突破提醒设置
        private void LoadBreakthroughSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings?.BreakthroughSettings != null)
                    {
                        _breakthroughSettings = settings.BreakthroughSettings;
                    }
                    else
                    {
                        _breakthroughSettings = new BreakthroughSettings();
                    }
                }
                else
                {
                    _breakthroughSettings = new BreakthroughSettings();
                }
                
                AppendLog("已加载突破提醒设置");
            }
            catch (Exception ex)
            {
                AppendLog($"加载突破提醒设置时出错: {ex.Message}");
                _breakthroughSettings = new BreakthroughSettings();
            }
        }

        // 检查并发送突破提醒
        private async Task CheckAndSendBreakthroughAlerts(List<RealtimeRankingViewModel> rankings)
        {
            try
            {
                // 如果没有设置Token或所有阈值都未启用，则不执行
                if (_breakthroughSettings.Tokens == null || 
                    _breakthroughSettings.Tokens.Count == 0 || 
                    (!_breakthroughSettings.Threshold1Enabled && 
                     !_breakthroughSettings.Threshold2Enabled && 
                     !_breakthroughSettings.Threshold3Enabled &&
                     !_breakthroughSettings.EnableHighLowBreakthrough))
                {
                    return;
                }

                foreach (var ranking in rankings)
                {
                    var symbolWithSuffix = ranking.Symbol + "USDT";
                    
                    // 检查涨跌幅突破
                    if (_breakthroughSettings.EnableNotifications)
                    {
                        // 计算百分比（转换为正数用于比较）
                        var percentage = Math.Abs(ranking.Percentage * 100);
                        var isUptrend = ranking.Percentage > 0;
                        
                        // 使用排名数据中的成交额
                        decimal volume = ranking.QuoteVolume;
                        
                        // 检查各个阈值
                        CheckThresholdAndCollectAlert(ranking.Symbol, percentage, isUptrend, 1, _breakthroughSettings.Threshold1, _breakthroughSettings.Threshold1Enabled, volume);
                        CheckThresholdAndCollectAlert(ranking.Symbol, percentage, isUptrend, 2, _breakthroughSettings.Threshold2, _breakthroughSettings.Threshold2Enabled, volume);
                        CheckThresholdAndCollectAlert(ranking.Symbol, percentage, isUptrend, 3, _breakthroughSettings.Threshold3, _breakthroughSettings.Threshold3Enabled, volume);
                    }

                    // 检查新高/新低突破
                    if (_breakthroughSettings.EnableHighLowBreakthrough && _isHighLowCacheInitialized)
                    {
                        lock (_highLowCacheLock)
                        {
                            if (_highLowCache.TryGetValue(symbolWithSuffix, out var highLowData))
                            {
                                var currentPrice = ranking.LastPrice;
                                
                                // 检查5日高低点
                                if (_breakthroughSettings.HighLowDays1Enabled && highLowData.TryGetValue(5, out var data5d))
                                {
                                    CheckHighLowBreakthrough(ranking.Symbol, currentPrice, data5d.High, data5d.Low, 5, ranking.QuoteVolume);
                                }
                                
                                // 检查10日高低点
                                if (_breakthroughSettings.HighLowDays2Enabled && highLowData.TryGetValue(10, out var data10d))
                                {
                                    CheckHighLowBreakthrough(ranking.Symbol, currentPrice, data10d.High, data10d.Low, 10, ranking.QuoteVolume);
                                }
                                
                                // 检查20日高低点
                                if (_breakthroughSettings.HighLowDays3Enabled && highLowData.TryGetValue(20, out var data20d))
                                {
                                    CheckHighLowBreakthrough(ranking.Symbol, currentPrice, data20d.High, data20d.Low, 20, ranking.QuoteVolume);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => AppendLog($"检查突破提醒时出错: {ex.Message}"));
            }
        }
        
        // 检查单个阈值并收集提醒
        private void CheckThresholdAndCollectAlert(string symbol, decimal percentage, bool isUptrend, int thresholdIndex, decimal thresholdValue, bool isEnabled, decimal volume)
        {
            if (!isEnabled) return;
            
            try
            {
                // 确保存在状态字典
                if (!_breakthroughSettings.LastExceededState.TryGetValue(symbol, out var stateDict))
                {
                    stateDict = new Dictionary<int, (bool Exceeded, decimal LastPercentage)>();
                    _breakthroughSettings.LastExceededState[symbol] = stateDict;
                }
                
                // 确保存在特定阈值的状态
                if (!stateDict.ContainsKey(thresholdIndex))
                {
                    // 初始化状态，但不触发提醒
                    stateDict[thresholdIndex] = (false, percentage);
                    return;
                }
                
                var (wasExceeded, lastPercentage) = stateDict[thresholdIndex];
                bool hasExceeded = percentage >= thresholdValue;
                
                // 检查是否从未突破到突破（且上次涨幅低于阈值，且不是第一次）
                if (hasExceeded && !wasExceeded && lastPercentage < thresholdValue && lastPercentage != 0)
                {
                    // 更新状态
                    stateDict[thresholdIndex] = (true, percentage);
                    
                    // 添加到待发送列表
                    var breakthroughEvent = new BreakthroughEvent
                    {
                        Symbol = symbol,
                        Percentage = percentage,
                        IsUptrend = isUptrend,
                        ThresholdValue = thresholdValue,
                        Volume = volume,
                        Timestamp = DateTime.Now
                    };
                    
                    lock (_pendingEventsLock)
                    {
                        if (isUptrend)
                            _pendingUptrends.Add(breakthroughEvent);
                        else
                            _pendingDowntrends.Add(breakthroughEvent);
                    }
                    
                    // 记录日志
                    var directionText = isUptrend ? "上涨" : "下跌";
                    Dispatcher.Invoke(() => AppendLog($"突破提醒收集：{symbol} {directionText}突破{thresholdValue}%（从{lastPercentage:F2}%到{percentage:F2}%），成交额：{FormatVolume(volume)}"));
                }
                // 如果从突破回到未突破，重置状态
                else if (!hasExceeded && wasExceeded)
                {
                    stateDict[thresholdIndex] = (false, percentage);
                }
                // 更新最新涨幅
                else
                {
                    stateDict[thresholdIndex] = (wasExceeded, percentage);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"处理突破提醒时出错: {ex.Message}"));
            }
        }
        
        // 添加新高/新低突破检查方法
        private void CheckHighLowBreakthrough(string symbol, decimal currentPrice, decimal highPrice, decimal lowPrice, int days, decimal volume)
        {
            try
            {
                // 确保存在状态字典
                if (!_breakthroughSettings.LastHighLowState.TryGetValue(symbol, out var stateDict))
                {
                    stateDict = new Dictionary<int, (bool ExceededHigh, bool ExceededLow, decimal LastHigh, decimal LastLow)>();
                    _breakthroughSettings.LastHighLowState[symbol] = stateDict;
                }

                // 确保存在特定天数的状态
                if (!stateDict.ContainsKey(days))
                {
                    stateDict[days] = (false, false, currentPrice, currentPrice);
                    return;
                }

                var state = stateDict[days];
                bool wasExceededHigh = state.ExceededHigh;
                bool wasExceededLow = state.ExceededLow;
                decimal lastHigh = state.LastHigh;
                decimal lastLow = state.LastLow;

                bool hasExceededHigh = currentPrice > highPrice;
                bool hasExceededLow = currentPrice < lowPrice;

                // 检查新高突破
                if (hasExceededHigh && !wasExceededHigh)
                {
                    var breakthroughEvent = new BreakthroughEvent
                    {
                        Symbol = symbol,
                        Percentage = (currentPrice - highPrice) / highPrice * 100,
                        IsUptrend = true,
                        ThresholdValue = days,
                        Volume = volume,
                        Timestamp = DateTime.Now,
                        IsHighLowBreakthrough = true,
                        Days = days,
                        IsHigh = true
                    };

                    lock (_pendingEventsLock)
                    {
                        _pendingHighBreaks.Add(breakthroughEvent);
                    }

                    Dispatcher.Invoke(() => AppendLog($"新高突破提醒：{symbol} 突破{days}日新高 {highPrice:F4} -> {currentPrice:F4}，成交额：{FormatVolume(volume)}"));
                }

                // 检查新低突破
                if (hasExceededLow && !wasExceededLow)
                {
                    var breakthroughEvent = new BreakthroughEvent
                    {
                        Symbol = symbol,
                        Percentage = (currentPrice - lowPrice) / lowPrice * 100,
                        IsUptrend = false,
                        ThresholdValue = days,
                        Volume = volume,
                        Timestamp = DateTime.Now,
                        IsHighLowBreakthrough = true,
                        Days = days,
                        IsHigh = false
                    };

                    lock (_pendingEventsLock)
                    {
                        _pendingLowBreaks.Add(breakthroughEvent);
                    }

                    Dispatcher.Invoke(() => AppendLog($"新低突破提醒：{symbol} 突破{days}日新低 {lowPrice:F4} -> {currentPrice:F4}，成交额：{FormatVolume(volume)}"));
                }

                // 更新状态
                stateDict[days] = (hasExceededHigh, hasExceededLow, Math.Max(currentPrice, lastHigh), Math.Min(currentPrice, lastLow));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"处理新高/新低突破提醒时出错: {ex.Message}"));
            }
        }
        
        // 定时发送所有待发送的通知
        private void SendPendingNotifications(object state)
        {
            List<BreakthroughEvent> uptrends, downtrends, highBreaks, lowBreaks;
            
            lock (_pendingEventsLock)
            {
                uptrends = new List<BreakthroughEvent>(_pendingUptrends);
                downtrends = new List<BreakthroughEvent>(_pendingDowntrends);
                highBreaks = new List<BreakthroughEvent>(_pendingHighBreaks);
                lowBreaks = new List<BreakthroughEvent>(_pendingLowBreaks);
                
                _pendingUptrends.Clear();
                _pendingDowntrends.Clear();
                _pendingHighBreaks.Clear();
                _pendingLowBreaks.Clear();
            }
            
            if (uptrends.Count == 0 && downtrends.Count == 0 && highBreaks.Count == 0 && lowBreaks.Count == 0)
                return;
                
            // 去重处理
            uptrends = uptrends.GroupBy(e => e.Symbol).Select(g => g.OrderByDescending(e => e.Percentage).First()).OrderByDescending(e => e.Percentage).ToList();
            downtrends = downtrends.GroupBy(e => e.Symbol).Select(g => g.OrderByDescending(e => e.Percentage).First()).OrderByDescending(e => e.Percentage).ToList();
            highBreaks = highBreaks.GroupBy(e => e.Symbol).Select(g => g.OrderByDescending(e => e.Percentage).First()).OrderByDescending(e => e.Percentage).ToList();
            lowBreaks = lowBreaks.GroupBy(e => e.Symbol).Select(g => g.OrderByDescending(e => e.Percentage).First()).OrderByDescending(e => e.Percentage).ToList();

            // 分别发送不同类型的提醒
            Task.Run(async () =>
            {
                try
                {
                    // 发送涨跌幅突破提醒
                    if (uptrends.Count > 0 || downtrends.Count > 0)
                    {
                        var title = "突破当日涨幅百分比提醒";
            var messageBuilder = new StringBuilder();
            
            if (uptrends.Count > 0)
            {
                messageBuilder.AppendLine("多头突破");
                for (int i = 0; i < uptrends.Count; i++)
                {
                    var evt = uptrends[i];
                    messageBuilder.AppendLine($"{i + 1}、{evt.Symbol}-{evt.Percentage:F2}%-{FormatVolume(evt.Volume)}");
                }
            }
            
            if (downtrends.Count > 0)
            {
                            if (uptrends.Count > 0) messageBuilder.AppendLine();
                messageBuilder.AppendLine("空头突破");
                for (int i = 0; i < downtrends.Count; i++)
                {
                    var evt = downtrends[i];
                    messageBuilder.AppendLine($"{i + 1}、{evt.Symbol}-{evt.Percentage:F2}%-{FormatVolume(evt.Volume)}");
                }
            }
            
                        await SendBreakthroughAlert(title, messageBuilder.ToString());
                        Dispatcher.Invoke(() => AppendLog($"已发送涨跌幅突破提醒：{uptrends.Count}个多头突破，{downtrends.Count}个空头突破"));
                    }

                    // 发送新高突破提醒
                    if (highBreaks.Count > 0)
                    {
                        var title = "突破N日提醒";
                        var messageBuilder = new StringBuilder();
                        messageBuilder.AppendLine("新高突破");
                        for (int i = 0; i < highBreaks.Count; i++)
                        {
                            var evt = highBreaks[i];
                            messageBuilder.AppendLine($"{i + 1}、{evt.Symbol}-{evt.Days}日新高-{evt.Percentage:F2}%-{FormatVolume(evt.Volume)}");
                        }

                        await SendBreakthroughAlert(title, messageBuilder.ToString());
                        Dispatcher.Invoke(() => AppendLog($"已发送新高突破提醒：{highBreaks.Count}个新高突破"));
                    }

                    // 发送新低突破提醒
                    if (lowBreaks.Count > 0)
                    {
                        var title = "突破N日提醒";
                        var messageBuilder = new StringBuilder();
                        messageBuilder.AppendLine("新低突破");
                        for (int i = 0; i < lowBreaks.Count; i++)
                        {
                            var evt = lowBreaks[i];
                            messageBuilder.AppendLine($"{i + 1}、{evt.Symbol}-{evt.Days}日新低-{evt.Percentage:F2}%-{FormatVolume(evt.Volume)}");
                        }

                        await SendBreakthroughAlert(title, messageBuilder.ToString());
                        Dispatcher.Invoke(() => AppendLog($"已发送新低突破提醒：{lowBreaks.Count}个新低突破"));
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"发送汇总突破提醒时出错: {ex.Message}"));
                }
            });
        }
        
        // 格式化成交额显示
        private string FormatVolume(decimal volume)
        {
            if (volume >= 1_000_000_000) // 大于等于10亿
                return $"{volume / 1_000_000_000:F2}B";
            else if (volume >= 1_000_000) // 大于等于100万
                return $"{volume / 1_000_000:F2}M";
            else if (volume >= 1_000) // 大于等于1000
                return $"{volume / 1_000:F2}K";
            else
                return $"{volume:F2}";
        }
        
        // 发送突破提醒
        private async Task SendBreakthroughAlert(string title, string message)
        {
            if (_breakthroughSettings.Tokens == null || _breakthroughSettings.Tokens.Count == 0)
                return;
                
            foreach (var token in _breakthroughSettings.Tokens)
            {
                try
                {
                    var url = $"https://wx.xtuis.cn/{token}.send";
                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("text", title),
                        new KeyValuePair<string, string>("desp", message)
                    });
                    
                    var response = await _httpClient.PostAsync(url, content);
                    var responseContent = await response.Content.ReadAsStringAsync();
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        Dispatcher.Invoke(() => AppendLog($"发送突破提醒失败: HTTP {(int)response.StatusCode} - {responseContent}"));
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"发送突破提醒给Token[{token}]时出错: {ex.Message}"));
                }
            }
        }

        private async void InitializeHighLowCacheAsync()
        {
            try
            {
                if (_binanceApiService == null || _rankingService == null) return;

                AppendLog("正在初始化新高/新低数据缓存...");
                
                // 获取所有活跃的永续合约
                var activeSymbols = await _binanceApiService.GetPerpetualSymbolsAsync();
                if (activeSymbols == null || !activeSymbols.Any())
                {
                    AppendLog("错误：无法获取有效的合约信息");
                    return;
                }

                // 获取当前时间
                var endTime = DateTime.Now;
                var startTime = endTime.AddDays(-20); // 获取20天的数据，以覆盖所有可能的周期

                // 并行处理所有交易对
                var tasks = activeSymbols.Select(async symbol =>
                {
                    try
                    {
                        // 获取K线数据
                        var klines = await _binanceApiService.GetKlinesAsync(symbol, "1d", startTime, endTime);
                        if (klines == null || !klines.Any()) return;

                        // 计算不同周期的高低点
                        var highLowData = new Dictionary<int, (decimal High, decimal Low)>();
                        
                        // 计算5日高低点
                        var klines5d = klines.TakeLast(5).ToList();
                        if (klines5d.Any())
                        {
                            highLowData[5] = (
                                klines5d.Max(k => k.High),
                                klines5d.Min(k => k.Low)
                            );
                        }

                        // 计算10日高低点
                        var klines10d = klines.TakeLast(10).ToList();
                        if (klines10d.Any())
                        {
                            highLowData[10] = (
                                klines10d.Max(k => k.High),
                                klines10d.Min(k => k.Low)
                            );
                        }

                        // 计算20日高低点
                        var klines20d = klines.TakeLast(20).ToList();
                        if (klines20d.Any())
                        {
                            highLowData[20] = (
                                klines20d.Max(k => k.High),
                                klines20d.Min(k => k.Low)
                            );
                        }

                        // 更新缓存
                        lock (_highLowCacheLock)
                        {
                            _highLowCache[symbol] = highLowData;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"初始化{symbol}的高低点数据时出错: {ex.Message}");
                    }
                });

                await Task.WhenAll(tasks);
                
                _isHighLowCacheInitialized = true;
                AppendLog("新高/新低数据缓存初始化完成");
            }
            catch (Exception ex)
            {
                AppendLog($"初始化新高/新低数据缓存时出错: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
            }
        }

        // 添加 AppendLog 方法
        private void AppendLog(string message)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                    txtLog.ScrollToEnd();
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "添加日志时出错");
            }
        }

        // 添加加载历史数据按钮的点击事件处理方法
        private async void btnLoadHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_rankingService == null)
            {
                AppendLog("错误：无法获取排名服务");
                return;
            }

            try
            {
                btnLoadHistory.IsEnabled = false;
                AppendLog("开始加载历史排名数据...");
                
                // 清空现有数据
                _historyTopGainers.Clear();
                _historyTopLosers.Clear();
                
                // 加载历史数据
                await LoadHistoryRankingsAsync();
                
                AppendLog("历史排名数据加载完成");
            }
            catch (Exception ex)
            {
                AppendLog($"加载历史排名数据失败：{ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常：{ex.InnerException.Message}");
                }
            }
            finally
            {
                btnLoadHistory.IsEnabled = true;
            }
        }
    }

    // 历史排名视图模型
    public class HistoryRankingViewModel
    {
        public DateTime Date { get; set; }
        public string TopGainer { get; set; } = string.Empty;
        public decimal TopGainerPercentage { get; set; }
        public string TopLoser { get; set; } = string.Empty;
        public decimal TopLoserPercentage { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // 实时排名视图模型
    public class RealtimeRankingViewModel
    {
        public int Rank { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public decimal LastPrice { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal Percentage { get; set; }
        public decimal QuoteVolume { get; set; }
        public string PercentageFormatted => $"{Percentage:P2}";
    }

    // 历史涨幅排名视图模型
    public class HistoryGainerDayViewModel
    {
        public DateTime Date { get; set; }
        public string Rank1 { get; set; } = string.Empty;
        public string Rank2 { get; set; } = string.Empty;
        public string Rank3 { get; set; } = string.Empty;
        public string Rank4 { get; set; } = string.Empty;
        public string Rank5 { get; set; } = string.Empty;
        public string Rank6 { get; set; } = string.Empty;
        public string Rank7 { get; set; } = string.Empty;
        public string Rank8 { get; set; } = string.Empty;
        public string Rank9 { get; set; } = string.Empty;
        public string Rank10 { get; set; } = string.Empty;
    }

    // 历史跌幅排名视图模型
    public class HistoryLoserDayViewModel
    {
        public DateTime Date { get; set; }
        public string Rank1 { get; set; } = string.Empty;
        public string Rank2 { get; set; } = string.Empty;
        public string Rank3 { get; set; } = string.Empty;
        public string Rank4 { get; set; } = string.Empty;
        public string Rank5 { get; set; } = string.Empty;
        public string Rank6 { get; set; } = string.Empty;
        public string Rank7 { get; set; } = string.Empty;
        public string Rank8 { get; set; } = string.Empty;
        public string Rank9 { get; set; } = string.Empty;
        public string Rank10 { get; set; } = string.Empty;
    }
    


    // 突破事件类
    public class BreakthroughEvent
    {
        public string Symbol { get; set; }
        public decimal Percentage { get; set; }
        public bool IsUptrend { get; set; }
        public decimal ThresholdValue { get; set; }
        public decimal Volume { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsHighLowBreakthrough { get; set; }
        public int Days { get; set; }
        public bool IsHigh { get; set; }
    }
} 