using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using TCServer.Core.Services;
using TCServer.Data.Repositories;
using TCServer.Common.Interfaces;
using TCServer.Common.Models;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using System.Threading.Tasks;
using System.Linq;
using MySql.Data.MySqlClient;

namespace TCServer;

public partial class MainWindow : Window
{
    private readonly IHost _host;
    private readonly KlineService _klineService;
    private readonly BinanceApiService _binanceApiService;
    private readonly AccountDataService _accountDataService;
    private readonly ServiceCoordinator _serviceCoordinator;
    private readonly ILogger<MainWindow> _logger;
    
    // 窗口实例引用，用于防止重复打开
    private AccountManagementWindow? _accountManagementWindow;
    private RankingWindow? _rankingWindow;

    public MainWindow(IHost host)
    {
        InitializeComponent();

        _host = host;
        _klineService = _host.Services.GetRequiredService<KlineService>();
        _binanceApiService = _host.Services.GetRequiredService<BinanceApiService>();
        _accountDataService = _host.Services.GetRequiredService<AccountDataService>();
        _serviceCoordinator = _host.Services.GetRequiredService<ServiceCoordinator>();
        _logger = _host.Services.GetRequiredService<ILogger<MainWindow>>();

        // 配置专门的K线服务日志输出到文本框（只显示K线相关日志）
        var loggerFactory = _host.Services.GetRequiredService<ILoggerFactory>();
        loggerFactory.AddProvider(new KlineServiceLoggerProvider(txtLog));
        
        // 注册进度更新事件
        _klineService.ProgressUpdated += KlineService_ProgressUpdated;

        // 初始化UI状态
        btnStart.IsEnabled = true;
        btnStop.IsEnabled = false;
        txtCurrentSymbol.Text = "等待启动服务...";
        txtStatus.Text = "服务状态：已停止";
        txtLastUpdate.Text = "最新数据日期：--";
        
        // 初始化进度条
        progressTotal.Value = 0;
        progressBatch.Value = 0;
        txtProgressTotal.Text = "0%";
        txtProgressBatch.Text = "0%";
        
        // 注册窗口事件
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }
    
    private void KlineService_ProgressUpdated(object? sender, ProgressUpdateEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // 计算总进度
            double totalProgress = e.TotalSymbols > 0 ? (double)e.ProcessedSymbols / e.TotalSymbols * 100 : 0;
                progressTotal.Value = totalProgress;
                txtProgressTotal.Text = $"{totalProgress:F1}%";
            
            // 计算批次进度
            double batchProgress = e.TotalBatches > 0 ? (double)e.CurrentBatch / e.TotalBatches * 100 : 0;
                progressBatch.Value = batchProgress;
                txtProgressBatch.Text = $"{batchProgress:F1}%";
            
            // 更新当前交易对
            txtCurrentSymbol.Text = $"当前处理: {e.CurrentSymbol} ({e.ProcessedSymbols}/{e.TotalSymbols})";
            
            // 更新状态
            txtStatus.Text = $"服务状态：运行中 (批次 {e.CurrentBatch}/{e.TotalBatches})";
        });
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 窗口加载时只更新基本状态，不执行数据库查询
            txtStatus.Text = "服务状态：已停止";
            txtLastUpdate.Text = "最新数据日期：--";
            txtCurrentSymbol.Text = "等待启动服务...";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化窗口时发生错误");
        }
    }

    private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            // 如果服务正在运行，先确认是否真的要关闭
            if (_klineService.IsFetching || _binanceApiService.IsRunning)
            {
                var result = MessageBox.Show(
                    "服务正在运行中，关闭窗口将中断所有服务。确定要关闭吗？", 
                    "确认关闭", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }
            
            // 停止所有服务
            await _klineService.StopAsync();
            await _binanceApiService.StopAsync();
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭窗口时发生错误");
        }
    }

    private async void btnStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            
            // 重置进度信息
            progressTotal.Value = 0;
            progressBatch.Value = 0;
            txtProgressTotal.Text = "0%";
            txtProgressBatch.Text = "0%";
            txtCurrentSymbol.Text = "正在启动服务...";
            
            // 使用服务协调器安全启动所有服务
            var success = await _serviceCoordinator.StartAllServicesAsync();
            
            if (success)
            {
                // 启用测试推送按钮
                btnTestPush.IsEnabled = true;
                txtCurrentSymbol.Text = "所有服务启动完成";
            }
            else
            {
                throw new InvalidOperationException("服务协调器启动失败");
            }
            
            await UpdateStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动服务时发生错误: {Message}", ex.Message);
            
            // 详细记录异常信息
            var errorDetails = $"启动失败详情:\n" +
                              $"异常类型: {ex.GetType().FullName}\n" +
                              $"异常消息: {ex.Message}\n" +
                              $"堆栈跟踪: {ex.StackTrace}";
            _logger.LogError(errorDetails);
            
            MessageBox.Show($"启动服务时发生错误：{ex.Message}\n\n详细信息请查看日志文件。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            
            // 发生错误时尝试安全停止所有服务
            txtCurrentSymbol.Text = "正在安全停止服务...";
            await _serviceCoordinator.StopAllServicesAsync();
            
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            txtCurrentSymbol.Text = "启动服务失败";
        }
    }

    private async void btnStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            btnStop.IsEnabled = false;
            txtCurrentSymbol.Text = "正在停止服务...";
            
            // 使用服务协调器安全停止所有服务
            await _serviceCoordinator.StopAllServicesAsync();
            
            // 禁用测试推送按钮
            btnTestPush.IsEnabled = false;
            
            btnStart.IsEnabled = true;
            txtCurrentSymbol.Text = "已停止";
            _logger.LogInformation("=== 所有服务已停止 ===");
            await UpdateStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止服务时发生错误: {Message}", ex.Message);
            MessageBox.Show($"停止服务时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
        }
    }



    private async void btnSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsWindow = new SettingsWindow();
            if (settingsWindow.ShowDialog() == true)
            {
                await UpdateStatusAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开设置窗口时发生错误");
            MessageBox.Show($"打开设置窗口时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void btnRanking_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 检查窗口是否已存在且未关闭
            if (_rankingWindow != null && _rankingWindow.IsLoaded)
            {
                // 激活现有窗口
                _rankingWindow.Activate();
                _rankingWindow.WindowState = WindowState.Normal;
                _logger.LogInformation("排名窗口已存在，激活现有窗口");
                return;
            }

            // 创建新窗口
            _rankingWindow = new RankingWindow();
            
            // 监听窗口关闭事件，清理引用
            _rankingWindow.Closed += (s, args) =>
            {
                _rankingWindow = null;
                _logger.LogInformation("排名窗口已关闭，清理引用");
            };
            
            _rankingWindow.Show();
            _logger.LogInformation("排名窗口已打开");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开排名窗口时发生错误");
            MessageBox.Show($"打开排名窗口时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            
            // 清理可能的无效引用
            _rankingWindow = null;
        }
    }

    private void btnAccountManagement_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 检查窗口是否已存在且未关闭
            if (_accountManagementWindow != null && _accountManagementWindow.IsLoaded)
            {
                // 激活现有窗口
                _accountManagementWindow.Activate();
                _accountManagementWindow.WindowState = WindowState.Normal;
                _logger.LogInformation("账户监管窗口已存在，激活现有窗口");
                return;
            }

            // 创建新窗口
            _accountManagementWindow = new AccountManagementWindow();
            
            // 监听窗口关闭事件，清理引用
            _accountManagementWindow.Closed += (s, args) =>
            {
                _accountManagementWindow = null;
                _logger.LogInformation("账户监管窗口已关闭，清理引用");
            };
            
            _accountManagementWindow.Show();
            _logger.LogInformation("账户监管窗口已打开");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开账户监管窗口时发生错误");
            MessageBox.Show($"打开账户监管窗口时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            
            // 清理可能的无效引用
            _accountManagementWindow = null;
        }
    }

    private async void btnTestPush_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            btnTestPush.IsEnabled = false;
            txtCurrentSymbol.Text = "正在测试推送...";
            
            var success = await _accountDataService.TriggerManualPushAsync();
            
            if (success)
            {
                MessageBox.Show("测试推送已发送！请检查您的微信。", "推送成功", MessageBoxButton.OK, MessageBoxImage.Information);
                txtCurrentSymbol.Text = "测试推送完成";
            }
            else
            {
                MessageBox.Show("测试推送失败，请检查配置和日志。", "推送失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtCurrentSymbol.Text = "测试推送失败";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "测试推送时发生错误");
            MessageBox.Show($"测试推送时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            txtCurrentSymbol.Text = "测试推送异常";
        }
        finally
        {
            btnTestPush.IsEnabled = true;
        }
    }

    private async Task UpdateStatusAsync()
    {
        try
        {
            // 如果服务未启动，只显示基本状态
            if (!btnStop.IsEnabled)
            {
                txtStatus.Text = "服务状态：已停止";
                txtLastUpdate.Text = "最新数据日期：--";
                return;
            }

            var configRepository = _host.Services.GetRequiredService<TCServer.Common.Interfaces.ISystemConfigRepository>();
            var klineRepository = _host.Services.GetRequiredService<TCServer.Common.Interfaces.IKlineRepository>();

            var fetchTimeConfig = await configRepository.GetConfigAsync("KlineFetchTime");
            var fetchTime = fetchTimeConfig?.Value ?? "未设置";

            var symbols = await klineRepository.GetAllSymbolsAsync();
            var latestDates = await Task.WhenAll(symbols.Select(async symbol =>
            {
                var date = await klineRepository.GetLatestKlineDateAsync(symbol);
                return new { Symbol = symbol, Date = date };
            }));

            var latestDate = latestDates
                .Where(x => x.Date.HasValue)
                .OrderByDescending(x => x.Date)
                .FirstOrDefault()?.Date;

            txtStatus.Text = $"服务状态：{(btnStop.IsEnabled ? "运行中" : "已停止")} | 获取时间：{fetchTime}";
            txtLastUpdate.Text = $"最新数据日期：{(latestDate?.ToString("yyyy-MM-dd") ?? "--")}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新状态时发生错误");
        }
    }
}

public class KlineServiceLoggerProvider : ILoggerProvider
{
    private readonly TextBox _textBox;

    public KlineServiceLoggerProvider(TextBox textBox)
    {
        _textBox = textBox;
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        return new KlineServiceLogger(_textBox, categoryName);
    }

    public void Dispose()
    {
    }
}

public class KlineServiceLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly TextBox _textBox;
    private readonly string _categoryName;

    public KlineServiceLogger(TextBox textBox, string categoryName)
    {
        _textBox = textBox;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // 基于categoryName过滤日志，只显示K线服务相关的日志
        // 过滤掉账户服务相关的日志
        if (_categoryName.Contains("AccountDataService") || 
            _categoryName.Contains("NotificationService") ||
            _categoryName.Contains("AccountManagementWindow"))
        {
            return; // 不显示账户服务相关日志
        }
        
        // 只显示以下服务的日志：
        // - KlineService
        // - BinanceApiService (但只显示K线相关的)
        // - RankingService
        // - MainWindow
        var allowedCategories = new[]
        {
            "TCServer.Core.Services.KlineService",
            "TCServer.Core.Services.RankingService", 
            "TCServer.MainWindow",
            "TCServer.SettingsWindow"
        };
        
        var message = formatter(state, exception);
        
        // 对于BinanceApiService，只显示K线相关的消息
        if (_categoryName.Contains("BinanceApiService"))
        {
            if (!message.Contains("K线") && 
                !message.Contains("Kline") && 
                !message.Contains("交易对") &&
                !message.Contains("获取到") &&
                !message.Contains("正在请求"))
            {
                return; // 过滤掉非K线相关的BinanceApi日志
            }
        }
        else if (!allowedCategories.Any(cat => _categoryName.Contains(cat)))
        {
            return; // 不在允许列表中的日志不显示
        }
        
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] {message}";
        
        // 增强处理异常信息
        if (exception != null)
        {
            logMessage += Environment.NewLine + $"异常: {exception.Message}";
            
            // 特别处理MySQL异常
            if (exception is MySql.Data.MySqlClient.MySqlException mysqlEx)
            {
                logMessage += Environment.NewLine + $"MySQL错误码: {mysqlEx.Number}, SQL状态: {mysqlEx.SqlState}";
            }
            
            // 记录内部异常
            if (exception.InnerException != null)
            {
                logMessage += Environment.NewLine + $"内部异常: {exception.InnerException.Message}";
                
                // 特别处理内部MySQL异常
                if (exception.InnerException is MySql.Data.MySqlClient.MySqlException innerMysqlEx)
                {
                    logMessage += Environment.NewLine + $"MySQL内部错误码: {innerMysqlEx.Number}, SQL状态: {innerMysqlEx.SqlState}";
                }
            }
            
            // 记录堆栈跟踪的前几行
            if (!string.IsNullOrEmpty(exception.StackTrace))
            {
                var stackLines = exception.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                var limitedStack = string.Join(Environment.NewLine, stackLines.Take(5));
                logMessage += Environment.NewLine + "堆栈跟踪:" + Environment.NewLine + limitedStack;
            }
        }

        try
        {
            _textBox.Dispatcher.Invoke(() =>
            {
                _textBox.AppendText(logMessage + Environment.NewLine);
                _textBox.ScrollToEnd();
            });
        }
        catch (Exception ex)
        {
            // 避免日志记录本身导致崩溃
            System.Diagnostics.Debug.WriteLine($"日志记录异常: {ex.Message}");
        }
    }
} 