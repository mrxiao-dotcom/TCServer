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
using TCServer.BreakthroughAlert.Services.Interfaces;
using TCServer.BreakthroughAlert.Models;

namespace TCServer
{
    public partial class RankingWindow : Window
    {
        private readonly IDailyRankingRepository _rankingRepository;
        private readonly RankingService _rankingService;
        private readonly BinanceApiService _binanceApiService;
        private readonly IBreakthroughMonitor _breakthroughMonitor;
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
        private bool _isBreakthroughRunning;  // 新增：突破推送运行状态
        
        // 突破提醒相关字段
        private BreakthroughSettings _breakthroughSettings;
        private readonly string _settingsFilePath = "settings.json";
        private readonly HttpClient _httpClient;
        
        // 突破事件收集字段
        private readonly List<BreakthroughEvent> _pendingUptrends = new List<BreakthroughEvent>();
        private readonly List<BreakthroughEvent> _pendingDowntrends = new List<BreakthroughEvent>();
        private readonly List<BreakthroughEvent> _pendingHighBreaks = new List<BreakthroughEvent>();
        private readonly List<BreakthroughEvent> _pendingLowBreaks = new List<BreakthroughEvent>();
        private readonly object _pendingEventsLock = new object();
        private Timer? _notificationTimer;
        private const int NOTIFICATION_INTERVAL_MS = 300000; // 5分钟 = 300000毫秒

        // 添加新高/新低数据缓存
        private readonly Dictionary<string, Dictionary<int, (decimal High, decimal Low)>> _highLowCache = new();
        private readonly object _highLowCacheLock = new object();
        private bool _isHighLowCacheInitialized = false;

        // 推送信息数据模型
        public class PushMessage
        {
            public string Timestamp { get; set; } = string.Empty;
            public string Symbol { get; set; } = string.Empty;
            public string Percentage { get; set; } = string.Empty;
            public string LastPrice { get; set; } = string.Empty;
            public string OpenPrice { get; set; } = string.Empty;
        }

        // 推送信息集合
        private readonly ObservableCollection<PushMessage> _pushBuffer = new();
        private readonly ObservableCollection<PushMessage> _pushSent = new();

        public RankingWindow()
        {
            InitializeComponent();
            
            // 获取服务
            var host = ((App)Application.Current).Host;
            _rankingRepository = host.Services.GetRequiredService<IDailyRankingRepository>();
            _rankingService = host.Services.GetRequiredService<RankingService>();
            _binanceApiService = host.Services.GetRequiredService<BinanceApiService>();
            _breakthroughMonitor = host.Services.GetRequiredService<IBreakthroughMonitor>();
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
            
            // 注释掉自动初始化缓存
            // InitializeHighLowCacheAsync();

            // 订阅突破监控服务的事件
            _breakthroughMonitor.OnBreakthrough += BreakthroughMonitor_OnBreakthrough;
            _breakthroughMonitor.OnStatusChanged += BreakthroughMonitor_OnStatusChanged;
            _breakthroughMonitor.OnError += BreakthroughMonitor_OnError;
        }
        
        // 处理排名服务日志事件
        private void RankingService_LogUpdated(object? sender, RankingLogEventArgs e)
        {
            Dispatcher.Invoke(() => AppendLog(e.ToString()));
        }

        // 处理排名更新事件
        private void RankingService_RankingUpdated(object? sender, RankingUpdateEventArgs e)
        {
            if (!_isBreakthroughRunning || e == null) return;

            try
            {
                // 处理突破事件
                if (e.TopGainers != null)
                {
                    foreach (var gainer in e.TopGainers)
                    {
                        var symbol = gainer.Symbol;
                        var percentage = gainer.Percentage;
                        
                        // 从实时排名数据中查找对应的价格信息
                        var realtimeData = _realtimeTopGainers.FirstOrDefault(r => r.Symbol == symbol) ??
                                         _realtimeTopLosers.FirstOrDefault(r => r.Symbol == symbol);
                        
                        if (realtimeData != null)
                        {
                            // 检查是否满足突破条件
                            if (_breakthroughSettings.Threshold1Enabled && Math.Abs(percentage) >= _breakthroughSettings.Threshold1 ||
                                _breakthroughSettings.Threshold2Enabled && Math.Abs(percentage) >= _breakthroughSettings.Threshold2 ||
                                _breakthroughSettings.Threshold3Enabled && Math.Abs(percentage) >= _breakthroughSettings.Threshold3)
                            {
                                // 添加到突破事件缓存
                                var breakthroughEvent = new BreakthroughEvent
                                {
                                    Symbol = symbol,
                                    ChangePercent = percentage * 100,
                                    Type = percentage > 0 ? BreakthroughType.UpThreshold : BreakthroughType.DownThreshold,
                                    ThresholdValue = Math.Abs(percentage) * 100,
                                    Volume = realtimeData.QuoteVolume,
                                    EventTime = DateTime.Now
                                };
                                
                                lock (_pendingEventsLock)
                                {
                                    if (percentage > 0)
                                        _pendingUptrends.Add(breakthroughEvent);
                                    else
                                        _pendingDowntrends.Add(breakthroughEvent);
                                }
                            }
                        }
                    }
                }

                if (e.TopLosers != null)
                {
                    // 跌幅突破事件已经在TopGainers中处理，这里不需要重复处理
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"处理排名更新事件时出错: {ex.Message}"));
                if (ex.InnerException != null)
                {
                    Dispatcher.Invoke(() => AppendLog($"内部异常: {ex.InnerException.Message}"));
                }
            }
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
                AppendLog("正在启动实时排名服务...");
                _isRealtimeRunning = true;
                _realtimeCts = new CancellationTokenSource();
                
                // 清空实时排名数据
                _realtimeTopGainers.Clear();
                _realtimeTopLosers.Clear();
                
                // 启动定时更新任务
                var updateTask = Task.Run(async () =>
                {
                    AppendLog("实时排名更新任务已启动");
                    int consecutiveErrors = 0;
                    const int MAX_CONSECUTIVE_ERRORS = 3;

                    try
                    {
                        while (!_realtimeCts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                AppendLog("正在获取最新行情数据...");
                                // 获取所有交易对的24小时行情数据
                                var tickers = await _binanceApiService.Get24hrTickerAsync();
                                if (tickers == null || !tickers.Any())
                                {
                                    AppendLog("警告：获取行情数据为空，等待下次更新");
                                    consecutiveErrors++;
                                    if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                                    {
                                        throw new Exception($"连续{MAX_CONSECUTIVE_ERRORS}次获取数据失败");
                                    }
                                    await Task.Delay(5000, _realtimeCts.Token);
                                    continue;
                                }

                                consecutiveErrors = 0; // 重置错误计数
                                AppendLog($"成功获取 {tickers.Count} 个交易对的行情数据");

                                var rankings = new List<RealtimeRankingViewModel>();
                                
                                foreach (var ticker in tickers)
                                {
                                    try
                                    {
                                        rankings.Add(new RealtimeRankingViewModel
                                        {
                                            Symbol = ticker.Symbol.Replace("USDT", ""), // 去掉USDT后缀
                                            LastPrice = ticker.LastPrice,
                                            OpenPrice = ticker.OpenPrice,
                                            Percentage = ticker.PriceChangePercent / 100, // 转换为小数
                                            QuoteVolume = ticker.QuoteVolume
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        AppendLog($"处理 {ticker.Symbol} 数据时出错: {ex.Message}");
                                    }
                                }

                                if (rankings.Any())
                                {
                                    // 更新UI
                                    await Dispatcher.InvokeAsync(async () =>
                                    {
                                        try
                                        {
                                            await UpdateRealtimeRankings(rankings);
                                            _lastUpdateTime = DateTime.Now;
                                            _nextUpdateTime = _lastUpdateTime.AddSeconds(5);
                                            UpdateStatusTexts();
                                            txtRealtimeStatus.Text = "实时排名状态：运行中";
                                            AppendLog($"已更新实时排名数据，下次更新：{_nextUpdateTime:HH:mm:ss}");
                                        }
                                        catch (Exception ex)
                                        {
                                            AppendLog($"更新UI时出错: {ex.Message}");
                                        }
                                    });
                                }
                                
                                // 等待到下一次更新
                                await Task.Delay(5000, _realtimeCts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                AppendLog("实时排名更新任务被取消");
                                break;
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"实时排名更新出错: {ex.Message}");
                                if (ex.InnerException != null)
                                {
                                    AppendLog($"内部异常: {ex.InnerException.Message}");
                                }
                                consecutiveErrors++;
                                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                                {
                                    throw new Exception($"连续{MAX_CONSECUTIVE_ERRORS}次更新失败，停止服务");
                                }
                                // 出错后等待一段时间再重试
                                await Task.Delay(5000, _realtimeCts.Token);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"实时排名任务发生严重错误: {ex.Message}");
                        if (ex.InnerException != null)
                        {
                            AppendLog($"内部异常: {ex.InnerException.Message}");
                        }
                        // 发生严重错误时，重置状态
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _isRealtimeRunning = false;
                            btnStartRealtime.IsEnabled = true;
                            btnStopRealtime.IsEnabled = false;
                            txtRealtimeStatus.Text = "实时排名状态：已停止";
                            AppendLog("实时排名服务已停止");
                        });
                    }
                }, _realtimeCts.Token);

                // 等待任务启动
                await Task.Delay(100);
                
                // 检查任务是否还在运行
                if (updateTask.IsFaulted)
                {
                    var exception = updateTask.Exception?.InnerException ?? updateTask.Exception;
                    throw new Exception($"实时排名任务启动失败: {exception?.Message}");
                }
                
                btnStartRealtime.IsEnabled = false;
                btnStopRealtime.IsEnabled = true;
                AppendLog("实时排名服务已成功启动");
            }
            catch (Exception ex)
            {
                _isRealtimeRunning = false;
                AppendLog($"启动实时排名失败: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
                
                // 恢复按钮状态
                btnStartRealtime.IsEnabled = true;
                btnStopRealtime.IsEnabled = false;
                txtRealtimeStatus.Text = "实时排名状态：启动失败";
            }
        }

        private async Task UpdateRealtimeRankings(List<RealtimeRankingViewModel> rankings)
        {
            try
            {
                // 清空现有数据
                _realtimeTopGainers.Clear();
                _realtimeTopLosers.Clear();
                
                if (rankings == null || !rankings.Any())
                {
                    AppendLog("警告：没有可用的排名数据");
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
                await CheckAndSendBreakthroughAlerts(sortedRankings);
            }
            catch (Exception ex)
            {
                AppendLog($"更新实时排名时出错: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
            }
        }

        private async Task LoadHistoryRankingsAsync()
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
                
                // 在启动实时排名之前初始化缓存
                if (!_isHighLowCacheInitialized)
                {
                    AppendLog("正在初始化新高/新低数据缓存...");
                    try 
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35)); // 35秒总超时
                        await Task.Run(async () =>
                        {
                            try
                            {
                                await InitializeHighLowCacheAsync();
                                if (!_isHighLowCacheInitialized)
                                {
                                    throw new Exception("缓存初始化失败");
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw new Exception("初始化过程超时");
                            }
                        }, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"初始化缓存失败: {ex.Message}");
                        if (ex.InnerException != null)
                        {
                            AppendLog($"内部异常: {ex.InnerException.Message}");
                        }
                        // 恢复按钮状态
                        btnStartRealtime.IsEnabled = true;
                        btnStopRealtime.IsEnabled = false;
                        return;
                    }
                }
                else
                {
                    AppendLog("新高/新低数据缓存已初始化，跳过初始化步骤");
                }
                
                // 启动实时排名
                await StartRealtimeRankingAsync();
            }
            catch (Exception ex)
            {
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
                // 禁用所有相关按钮，防止重复点击
                btnStartRealtime.IsEnabled = false;
                btnStopRealtime.IsEnabled = false;
                btnStartBreakthrough.IsEnabled = false;
                btnStopBreakthrough.IsEnabled = false;
                
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
                // 确保按钮状态正确
                btnStartRealtime.IsEnabled = true;
                btnStopRealtime.IsEnabled = false;
                btnStartBreakthrough.IsEnabled = true;
                btnStopBreakthrough.IsEnabled = false;
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
                AppendLog("正在停止实时排名...");
                
                // 先停止突破推送
                if (_isBreakthroughRunning)
                {
                    try
                    {
                        AppendLog("正在停止突破推送...");
                        _isBreakthroughRunning = false;
                        if (_rankingService != null)
                        {
                            _rankingService.RankingUpdated -= RankingService_RankingUpdated;
                        }
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            btnStartBreakthrough.IsEnabled = true;
                            btnStopBreakthrough.IsEnabled = false;
                            await _breakthroughMonitor.StopMonitoringAsync();
                        });
                        AppendLog("突破推送已停止");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"停止突破推送时出错: {ex.Message}");
                    }
                }

                // 停止实时排名
                _isRealtimeRunning = false;
                
                // 取消实时更新任务
                if (_realtimeCts != null)
                {
                    try
                    {
                        _realtimeCts.Cancel();
                        await Task.Delay(1000); // 给任务一些时间来清理
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"取消实时更新任务时出错: {ex.Message}");
                    }
                    finally
                    {
                        _realtimeCts.Dispose();
                        _realtimeCts = null;
                    }
                }

                // 清空实时排名数据并更新UI
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        _realtimeTopGainers.Clear();
                        _realtimeTopLosers.Clear();
                        
                        // 更新UI状态
                        btnStartRealtime.IsEnabled = true;
                        btnStopRealtime.IsEnabled = false;
                        txtStatus.Text = "就绪";
                        txtRealtimeStatus.Text = "实时排名状态：已停止";
                        txtLastUpdateTime.Text = "最后更新时间：--";
                        txtNextUpdateTime.Text = "下次更新时间：--";
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"更新UI状态时出错: {ex.Message}");
                    }
                });
                
                AppendLog("实时排名已停止");
            }
            catch (Exception ex)
            {
                AppendLog($"停止实时排名时出错: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
                
                // 确保UI状态正确
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        btnStartRealtime.IsEnabled = true;
                        btnStopRealtime.IsEnabled = false;
                        txtRealtimeStatus.Text = "实时排名状态：停止出错";
                    });
                }
                catch (Exception uiEx)
                {
                    AppendLog($"更新UI状态时出错: {uiEx.Message}");
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
                    await LoadHistoryRankingsAsync();
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
                    _rankingService.RankingUpdated -= RankingService_RankingUpdated;
                }
                
                // 取消订阅突破监控服务的事件
                _breakthroughMonitor.OnBreakthrough -= BreakthroughMonitor_OnBreakthrough;
                _breakthroughMonitor.OnStatusChanged -= BreakthroughMonitor_OnStatusChanged;
                _breakthroughMonitor.OnError -= BreakthroughMonitor_OnError;
                
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
                        ChangePercent = percentage * 100,
                        Type = isUptrend ? BreakthroughType.UpThreshold : BreakthroughType.DownThreshold,
                        ThresholdValue = Math.Abs(percentage) * 100,
                        Volume = volume,
                        EventTime = DateTime.Now
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
                        ChangePercent = (currentPrice - highPrice) / highPrice * 100,
                        Type = BreakthroughType.NewHigh,
                        ThresholdValue = days,
                        Volume = volume,
                        EventTime = DateTime.Now,
                        Days = days
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
                        ChangePercent = (currentPrice - lowPrice) / lowPrice * 100,
                        Type = BreakthroughType.NewLow,
                        ThresholdValue = days,
                        Volume = volume,
                        EventTime = DateTime.Now,
                        Days = days
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
        
        // 修改发送待处理通知的方法
        private void SendPendingNotifications(object? state)
        {
            List<BreakthroughEvent> uptrends, downtrends, highBreaks, lowBreaks;
            
            lock (_pendingEventsLock)
            {
                // 获取当前缓存的所有事件
                uptrends = new List<BreakthroughEvent>(_pendingUptrends);
                downtrends = new List<BreakthroughEvent>(_pendingDowntrends);
                highBreaks = new List<BreakthroughEvent>(_pendingHighBreaks);
                lowBreaks = new List<BreakthroughEvent>(_pendingLowBreaks);
                
                // 清空缓存
                _pendingUptrends.Clear();
                _pendingDowntrends.Clear();
                _pendingHighBreaks.Clear();
                _pendingLowBreaks.Clear();
            }
            
            // 如果没有待发送的通知，直接返回
            if (uptrends.Count == 0 && downtrends.Count == 0 && highBreaks.Count == 0 && lowBreaks.Count == 0)
                return;
                
            // 去重处理：对于每个交易对，只保留涨幅/跌幅最大的事件
            uptrends = uptrends.GroupBy(e => e.Symbol)
                             .Select(g => g.OrderByDescending(e => e.Percentage).First())
                             .OrderByDescending(e => e.Percentage)
                             .ToList();
            downtrends = downtrends.GroupBy(e => e.Symbol)
                                 .Select(g => g.OrderByDescending(e => e.Percentage).First())
                                 .OrderByDescending(e => e.Percentage)
                                 .ToList();
            highBreaks = highBreaks.GroupBy(e => e.Symbol)
                                 .Select(g => g.OrderByDescending(e => e.Percentage).First())
                                 .OrderByDescending(e => e.Percentage)
                                 .ToList();
            lowBreaks = lowBreaks.GroupBy(e => e.Symbol)
                               .Select(g => g.OrderByDescending(e => e.Percentage).First())
                               .OrderByDescending(e => e.Percentage)
                               .ToList();

            // 分别发送不同类型的提醒
            Task.Run(async () =>
            {
                try
                {
                    // 发送涨跌幅突破提醒
                    if (uptrends.Count > 0 || downtrends.Count > 0)
                    {
                        var title = "突破当日涨跌幅提醒";
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

                    // 发送新高/新低突破提醒
                    if (highBreaks.Count > 0 || lowBreaks.Count > 0)
                    {
                        var title = "突破N日高低点提醒";
                        var messageBuilder = new StringBuilder();
                        
                        if (highBreaks.Count > 0)
                        {
                            messageBuilder.AppendLine("新高突破");
                            for (int i = 0; i < highBreaks.Count; i++)
                            {
                                var evt = highBreaks[i];
                                messageBuilder.AppendLine($"{i + 1}、{evt.Symbol}-{evt.Days}日新高-{evt.Percentage:F2}%-{FormatVolume(evt.Volume)}");
                            }
                        }
                        
                        if (lowBreaks.Count > 0)
                        {
                            if (highBreaks.Count > 0) messageBuilder.AppendLine();
                            messageBuilder.AppendLine("新低突破");
                            for (int i = 0; i < lowBreaks.Count; i++)
                            {
                                var evt = lowBreaks[i];
                                messageBuilder.AppendLine($"{i + 1}、{evt.Symbol}-{evt.Days}日新低-{evt.Percentage:F2}%-{FormatVolume(evt.Volume)}");
                            }
                        }
                        
                        await SendBreakthroughAlert(title, messageBuilder.ToString());
                        Dispatcher.Invoke(() => AppendLog($"已发送高低点突破提醒：{highBreaks.Count}个新高突破，{lowBreaks.Count}个新低突破"));
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"发送汇总突破提醒时出错: {ex.Message}"));
                    if (ex.InnerException != null)
                    {
                        Dispatcher.Invoke(() => AppendLog($"内部异常: {ex.InnerException.Message}"));
                    }
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

        private async Task InitializeHighLowCacheAsync()
        {
            if (_isHighLowCacheInitialized)
            {
                AppendLog("新高/新低数据缓存已经初始化，跳过初始化步骤");
                return;
            }

            try
            {
                if (_binanceApiService == null || _rankingService == null)
                {
                    throw new Exception("无法获取必要的服务");
                }

                AppendLog("开始获取行情数据...");
                
                // 使用超时任务包装API调用
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // 30秒超时
                try
                {
                    // 获取所有交易对的24小时行情数据
                    var tickers = await Task.Run(async () =>
                    {
                        try
                        {
                            AppendLog("正在获取永续合约列表...");
                            var result = await _binanceApiService.Get24hrTickerAsync();
                            if (result == null || !result.Any())
                            {
                                throw new Exception("获取行情数据失败：返回数据为空");
                            }
                            return result;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            AppendLog($"获取行情数据时出错: {ex.Message}");
                            throw;
                        }
                    }, cts.Token);

                    AppendLog($"成功获取 {tickers.Count} 个交易对的行情数据");

                    // 更新缓存
                    lock (_highLowCacheLock)
                    {
                        _highLowCache.Clear(); // 清空现有缓存
                        foreach (var ticker in tickers)
                        {
                            var highLowData = new Dictionary<int, (decimal High, decimal Low)>();
                            
                            // 使用24小时数据作为5日高低点
                            highLowData[5] = (
                                ticker.HighPrice,
                                ticker.LowPrice
                            );
                            
                            // 使用24小时数据作为10日高低点
                            highLowData[10] = (
                                ticker.HighPrice,
                                ticker.LowPrice
                            );
                            
                            // 使用24小时数据作为20日高低点
                            highLowData[20] = (
                                ticker.HighPrice,
                                ticker.LowPrice
                            );

                            _highLowCache[ticker.Symbol] = highLowData;
                        }
                    }
                    
                    _isHighLowCacheInitialized = true;
                    AppendLog($"新高/新低数据缓存初始化完成，共缓存 {_highLowCache.Count} 个交易对的数据");
                }
                catch (OperationCanceledException)
                {
                    throw new Exception("获取行情数据超时，请检查网络连接");
                }
            }
            catch (Exception ex)
            {
                _isHighLowCacheInitialized = false; // 确保初始化失败时重置状态
                AppendLog($"初始化新高/新低数据缓存时出错: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
                throw; // 重新抛出异常，让调用者处理
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

        private async void btnStartBreakthrough_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnStartBreakthrough.IsEnabled = false;
                btnStopBreakthrough.IsEnabled = true;
                
                // 加载突破推送设置
                if (File.Exists(_settingsFilePath))
                {
                    var settingsJson = await File.ReadAllTextAsync(_settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(settingsJson);
                    if (settings?.BreakthroughSettings != null)
                    {
                        // 转换为BreakthroughConfig
                        var config = new BreakthroughConfig
                        {
                            UpThresholds = new List<ThresholdConfig>
                            {
                                new() { Value = settings.BreakthroughSettings.Threshold1, IsEnabled = settings.BreakthroughSettings.Threshold1Enabled, Description = $"{settings.BreakthroughSettings.Threshold1}%涨幅" },
                                new() { Value = settings.BreakthroughSettings.Threshold2, IsEnabled = settings.BreakthroughSettings.Threshold2Enabled, Description = $"{settings.BreakthroughSettings.Threshold2}%涨幅" },
                                new() { Value = settings.BreakthroughSettings.Threshold3, IsEnabled = settings.BreakthroughSettings.Threshold3Enabled, Description = $"{settings.BreakthroughSettings.Threshold3}%涨幅" }
                            },
                            DownThresholds = new List<ThresholdConfig>
                            {
                                new() { Value = -settings.BreakthroughSettings.Threshold1, IsEnabled = settings.BreakthroughSettings.Threshold1Enabled, Description = $"{settings.BreakthroughSettings.Threshold1}%跌幅" },
                                new() { Value = -settings.BreakthroughSettings.Threshold2, IsEnabled = settings.BreakthroughSettings.Threshold2Enabled, Description = $"{settings.BreakthroughSettings.Threshold2}%跌幅" },
                                new() { Value = -settings.BreakthroughSettings.Threshold3, IsEnabled = settings.BreakthroughSettings.Threshold3Enabled, Description = $"{settings.BreakthroughSettings.Threshold3}%跌幅" }
                            },
                            NewHighConfigs = new List<NewHighConfig>
                            {
                                new() { Days = settings.BreakthroughSettings.HighLowDays1, IsEnabled = settings.BreakthroughSettings.HighLowDays1Enabled, Description = $"{settings.BreakthroughSettings.HighLowDays1}天新高" },
                                new() { Days = settings.BreakthroughSettings.HighLowDays2, IsEnabled = settings.BreakthroughSettings.HighLowDays2Enabled, Description = $"{settings.BreakthroughSettings.HighLowDays2}天新高" },
                                new() { Days = settings.BreakthroughSettings.HighLowDays3, IsEnabled = settings.BreakthroughSettings.HighLowDays3Enabled, Description = $"{settings.BreakthroughSettings.HighLowDays3}天新高" }
                            },
                            NewLowConfigs = new List<NewLowConfig>
                            {
                                new() { Days = settings.BreakthroughSettings.HighLowDays1, IsEnabled = settings.BreakthroughSettings.HighLowDays1Enabled, Description = $"{settings.BreakthroughSettings.HighLowDays1}天新低" },
                                new() { Days = settings.BreakthroughSettings.HighLowDays2, IsEnabled = settings.BreakthroughSettings.HighLowDays2Enabled, Description = $"{settings.BreakthroughSettings.HighLowDays2}天新低" },
                                new() { Days = settings.BreakthroughSettings.HighLowDays3, IsEnabled = settings.BreakthroughSettings.HighLowDays3Enabled, Description = $"{settings.BreakthroughSettings.HighLowDays3}天新低" }
                            },
                            NotificationConfig = new NotificationConfig
                            {
                                NotificationUrl = settings.BreakthroughSettings.NotificationUrl,
                                MessageTemplate = "{symbol} {type}突破提醒：当前价格{price}，变化幅度{change}%，成交额{volume}",
                                RetryCount = 3,
                                RetryIntervalSeconds = 5
                            }
                        };

                        // 更新配置并启动监控
                        await _breakthroughMonitor.UpdateConfigAsync(config);
                        await _breakthroughMonitor.StartMonitoringAsync();
                        _isBreakthroughRunning = true;
                        AppendLog("突破推送服务已启动");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"启动突破推送失败: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
                
                // 恢复按钮状态
                btnStartBreakthrough.IsEnabled = true;
                btnStopBreakthrough.IsEnabled = false;
            }
        }

        private async void btnStopBreakthrough_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnStartBreakthrough.IsEnabled = false;
                btnStopBreakthrough.IsEnabled = false;
                
                // 停止突破推送
                await _breakthroughMonitor.StopMonitoringAsync();
                _isBreakthroughRunning = false;
                
                AppendLog("突破推送服务已停止");
            }
            catch (Exception ex)
            {
                AppendLog($"停止突破推送失败: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AppendLog($"内部异常: {ex.InnerException.Message}");
                }
            }
            finally
            {
                btnStartBreakthrough.IsEnabled = true;
                btnStopBreakthrough.IsEnabled = false;
            }
        }

        private void BreakthroughMonitor_OnBreakthrough(object? sender, BreakthroughEvent e)
        {
            Dispatcher.Invoke(() =>
            {
                var direction = e.Type switch
                {
                    BreakthroughType.UpThreshold => "上涨",
                    BreakthroughType.DownThreshold => "下跌",
                    BreakthroughType.NewHigh => "新高",
                    BreakthroughType.NewLow => "新低",
                    _ => "未知"
                };
                AppendLog($"突破提醒：{e.Symbol} {direction}突破 {e.Description}，当前价格：{e.CurrentPrice:F8}，变化幅度：{e.ChangePercent:F2}%，成交额：{FormatVolume(e.Volume)}");
            });
        }

        private void BreakthroughMonitor_OnStatusChanged(object? sender, MonitorStatus status)
        {
            Dispatcher.Invoke(() =>
            {
                var statusText = status switch
                {
                    MonitorStatus.Running => "运行中",
                    MonitorStatus.Stopped => "已停止",
                    MonitorStatus.Error => "错误",
                    _ => "未知"
                };
                AppendLog($"突破推送服务状态变更：{statusText}");
            });
        }

        private void BreakthroughMonitor_OnError(object? sender, Exception e)
        {
            Dispatcher.Invoke(() =>
            {
                AppendLog($"突破推送服务错误：{e.Message}");
                if (e.InnerException != null)
                {
                    AppendLog($"内部异常：{e.InnerException.Message}");
                }
            });
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
} 