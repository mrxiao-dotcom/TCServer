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
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(IHost host)
    {
        InitializeComponent();

        _host = host;
        _klineService = _host.Services.GetRequiredService<KlineService>();
        _binanceApiService = _host.Services.GetRequiredService<BinanceApiService>();
        _logger = _host.Services.GetRequiredService<ILogger<MainWindow>>();

        // 配置日志输出到文本框
        var loggerFactory = _host.Services.GetRequiredService<ILoggerFactory>();
        loggerFactory.AddProvider(new TextBoxLoggerProvider(txtLog));
        
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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
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
            
            // 先启动BinanceApiService
            await _binanceApiService.StartAsync();
            
            // 然后启动KlineService
            await _klineService.StartAsync();
            await UpdateStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动服务时发生错误");
            MessageBox.Show($"启动服务时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            
            // 发生错误时尝试停止服务
            try
            {
                await _binanceApiService.StopAsync();
                await _klineService.StopAsync();
            }
            catch { }
            
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
            
            // 先停止KlineService
            await _klineService.StopAsync();
            
            // 然后停止BinanceApiService
            await _binanceApiService.StopAsync();
            
            btnStart.IsEnabled = true;
            txtCurrentSymbol.Text = "已停止";
            await UpdateStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止服务时发生错误");
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
            var rankingWindow = new RankingWindow();
            rankingWindow.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开排名窗口时发生错误");
            MessageBox.Show($"打开排名窗口时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

public class TextBoxLoggerProvider : ILoggerProvider
{
    private readonly TextBox _textBox;

    public TextBoxLoggerProvider(TextBox textBox)
    {
        _textBox = textBox;
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        return new TextBoxLogger(_textBox);
    }

    public void Dispose()
    {
    }
}

public class TextBoxLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly TextBox _textBox;

    public TextBoxLogger(TextBox textBox)
    {
        _textBox = textBox;
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
        var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] {formatter(state, exception)}";
        
        // 增强处理异常信息
        if (exception != null)
        {
            message += Environment.NewLine + $"异常: {exception.Message}";
            
            // 特别处理MySQL异常
            if (exception is MySql.Data.MySqlClient.MySqlException mysqlEx)
            {
                message += Environment.NewLine + $"MySQL错误码: {mysqlEx.Number}, SQL状态: {mysqlEx.SqlState}";
            }
            
            // 记录内部异常
            if (exception.InnerException != null)
            {
                message += Environment.NewLine + $"内部异常: {exception.InnerException.Message}";
                
                // 特别处理内部MySQL异常
                if (exception.InnerException is MySql.Data.MySqlClient.MySqlException innerMysqlEx)
                {
                    message += Environment.NewLine + $"MySQL内部错误码: {innerMysqlEx.Number}, SQL状态: {innerMysqlEx.SqlState}";
                }
            }
            
            // 记录堆栈跟踪的前几行
            if (!string.IsNullOrEmpty(exception.StackTrace))
            {
                var stackLines = exception.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                var limitedStack = string.Join(Environment.NewLine, stackLines.Take(5));
                message += Environment.NewLine + "堆栈跟踪:" + Environment.NewLine + limitedStack;
            }
        }

        _textBox.Dispatcher.Invoke(() =>
        {
            _textBox.AppendText(message + Environment.NewLine);
            _textBox.ScrollToEnd();
        });
    }
} 