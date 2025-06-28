using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.Debug;
using TCServer.Core.Services;
using TCServer.Data.Repositories;
using TCServer.Common.Interfaces;
using System.Diagnostics;
using System.Threading;
using MySql.Data.MySqlClient;
using System.Threading.Tasks;
using System.Security.Authentication;
using TCServer.BreakthroughAlert.Services;
using TCServer.BreakthroughAlert.Services.Interfaces;
using TCServer.Data.Repositories;

namespace TCServer;

public partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;
    private static Mutex? _mutex;

    public IHost Host => _host ?? throw new InvalidOperationException("Host未初始化");

    public App()
    {
        try
        {
            // 设置全局异常处理
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            
            // 检查是否已经有实例在运行
            _mutex = new Mutex(true, "TCServer.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("应用程序已经在运行中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(1);
                return;
            }

            // 配置日志
            var logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.Get24hrTickerAsync", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.GetExchangeInfoAsync", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.GetKlinesAsync", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.GetAllPricesAsync", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.GetAllBookTickersAsync", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.GetAllTickersAsync", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.GetAllSymbolsAsync", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.GetAllSymbolsInfoAsync", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.GetAllSymbolsInfoAsync.GetExchangeInfoAsync", LogEventLevel.Warning)
                .MinimumLevel.Override("TCServer.Core.Services.BinanceApiService.GetAllSymbolsInfoAsync.GetExchangeInfoAsync.AddSymbol", LogEventLevel.Warning)
                .WriteTo.File("logs/app.log", 
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception:j}")
                .WriteTo.File("logs/errors.log", 
                    restrictedToMinimumLevel: LogEventLevel.Error, 
                    rollingInterval: RollingInterval.Day)
                .WriteTo.Debug()
                .Enrich.FromLogContext()
                .CreateLogger();

            // 创建Host
            var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder();
            _logger?.LogInformation("Host.Builder创建完成");

            builder.ConfigureServices((context, services) =>
            {
                try
                {
                    // 配置日志
                    services.AddLogging(builder =>
                    {
                        builder.AddSerilog(logger);
                        builder.AddDebug();
                    });

                    // 注册 Serilog.ILogger
                    services.AddSingleton<Serilog.ILogger>(logger);

                    // 注册 HttpClient
                    services.AddHttpClient();

                    // 注册基础服务
                    services.AddSingleton<ISystemConfigRepository, SystemConfigRepository>();
                    services.AddSingleton<IDailyRankingRepository, DailyRankingRepository>();
                    services.AddSingleton<IAccountRepository, AccountRepository>();
                    
                    // 注册BinanceApiService为单例
                    services.AddSingleton<BinanceApiService>();
                    
                    // 注册突破监控相关服务
                    services.AddSingleton<IFileStorageService, FileStorageService>();
                    services.AddSingleton<IAlertMessageService, AlertMessageService>();
                    services.AddSingleton<IBreakthroughMonitor, BreakthroughMonitor>();
                    
                    // 注册其他服务
                    services.AddSingleton<IKlineRepository, KlineRepository>();
                    services.AddSingleton<KlineService>();
                    services.AddSingleton<RankingService>();
                    services.AddTransient<MainWindow>();
                    
                    // 注册账户数据服务（单例模式，防止多个实例导致重复推送）
                    services.AddSingleton<AccountDataService>();
                    
                    // 注册推送服务
                    services.AddSingleton<NotificationService>();
                    
                    // 注册服务协调器
                    services.AddSingleton<ServiceCoordinator>();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "服务注册失败");
                    throw;
                }
            });

            _logger?.LogInformation("开始构建Host...");
            _host = builder.Build();
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            
            // 设置全局ServiceProvider
            ServiceProviderAccessor.ServiceProvider = _host.Services;
            
            _logger.LogInformation("Host构建完成");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"应用程序初始化失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            if (_host == null)
            {
                throw new InvalidOperationException("Host未初始化");
            }

            _logger?.LogInformation("正在启动应用程序...");
            
            try
            {
                _logger?.LogInformation("开始启动Host...");
                
                // 在启动Host前检查数据库连接
                await CheckInitialDatabaseState();
                
                await _host.StartAsync();
                _logger?.LogInformation("Host启动完成");
                
                // 启动数据库连接池监控
                StartDbConnectionPoolMonitoring();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Host启动失败");
                throw;
            }
            
            try
            {
                // 手动创建并显示主窗口
                var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                _logger?.LogInformation("主窗口创建完成");
                
                mainWindow.Show();
                _logger?.LogInformation("主窗口显示完成");
                
                // 主窗口显示后再次检查数据库状态
                await CheckInitialDatabaseState();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "主窗口创建/显示失败");
                throw;
            }
            
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "启动应用程序时发生错误");
            MessageBox.Show($"启动应用程序时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // 检查初始数据库状态
    private async Task CheckInitialDatabaseState()
    {
        try
        {
            var connectionString = TCServer.Data.Repositories.DatabaseHelper.GetOptimizedConnectionString();
            using var connection = new MySql.Data.MySqlClient.MySqlConnection(connectionString);
            
            await connection.OpenAsync();
            
            // 使用COALESCE替代IFNULL，并添加默认值
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT 
                    COALESCE(COUNT(*), 0) as total_connections,
                    COALESCE(SUM(CASE WHEN command = 'Sleep' THEN 1 ELSE 0 END), 0) as sleeping_connections,
                    COALESCE(SUM(CASE WHEN command != 'Sleep' THEN 1 ELSE 0 END), 0) as active_connections,
                    COALESCE(
                        GROUP_CONCAT(
                            CONCAT(
                                'ID:', id, 
                                ',User:', user,
                                ',Host:', host,
                                ',Command:', command,
                                ',Time:', time,
                                ',State:', state,
                                ',Info:', COALESCE(info, '')
                            )
                            SEPARATOR '|'
                        ),
                        ''
                    ) as connection_details
                FROM information_schema.processlist
                WHERE user = CURRENT_USER()";
            
            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                // 使用GetValue和Convert来安全地获取值
                var totalConnections = Convert.ToInt32(reader.GetValue(0));
                var sleepingConnections = Convert.ToInt32(reader.GetValue(1));
                var activeConnections = Convert.ToInt32(reader.GetValue(2));
                var connectionDetails = Convert.ToString(reader.GetValue(3)) ?? "";
                
                _logger?.LogInformation(
                    "数据库初始状态检查:\n" +
                    $"1. 当前用户连接统计:\n" +
                    $"   - 总连接数: {totalConnections}\n" +
                    $"   - 休眠连接: {sleepingConnections}\n" +
                    $"   - 活动连接: {activeConnections}\n" +
                    "2. 连接详情:\n" +
                    (string.IsNullOrEmpty(connectionDetails) ? 
                        "   当前没有活跃连接" : 
                        string.Join("\n", connectionDetails.Split('|').Select(x => "   " + x))));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "检查数据库初始状态时出错: {Message}", ex.Message);
        }
    }

    // 数据库连接池监控
    private System.Threading.Timer? _dbMonitorTimer;
    private void StartDbConnectionPoolMonitoring()
    {
        // 每30秒检查一次数据库连接池状态
        _dbMonitorTimer = new System.Threading.Timer(CheckDbConnectionPool, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        _logger?.LogInformation("数据库连接池监控已启动");
    }

    private void CheckDbConnectionPool(object? state)
    {
        try
        {
            var connectionString = TCServer.Data.Repositories.DatabaseHelper.GetOptimizedConnectionString();
            using var connection = new MySql.Data.MySqlClient.MySqlConnection(connectionString);
            
            var timeoutTask = Task.Delay(10000);
            var connectionTask = connection.OpenAsync();
            
            if (Task.WhenAny(connectionTask, timeoutTask).Result == timeoutTask)
            {
                _logger?.LogWarning("数据库连接池状态检查超时");
                return;
            }
            
            if (connectionTask.IsFaulted)
            {
                _logger?.LogWarning(connectionTask.Exception, "数据库连接池状态检查失败");
                return;
            }
            
            // 使用COALESCE替代IFNULL，并添加默认值
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT 
                    COALESCE(COUNT(*), 0) as total_connections,
                    COALESCE(SUM(CASE WHEN command = 'Sleep' THEN 1 ELSE 0 END), 0) as sleeping_connections,
                    COALESCE(SUM(CASE WHEN command != 'Sleep' THEN 1 ELSE 0 END), 0) as active_connections,
                    COALESCE(MAX(time), 0) as max_connection_time,
                    COALESCE(
                        GROUP_CONCAT(
                            CONCAT(
                                'ID:', id, 
                                ',User:', user,
                                ',Host:', host,
                                ',Command:', command,
                                ',Time:', time,
                                ',State:', state,
                                ',Info:', COALESCE(info, '')
                            )
                            SEPARATOR '|'
                        ),
                        ''
                    ) as connection_details
                FROM information_schema.processlist
                WHERE user = CURRENT_USER()";
            
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                // 使用GetValue和Convert来安全地获取值
                var totalConnections = Convert.ToInt32(reader.GetValue(0));
                var sleepingConnections = Convert.ToInt32(reader.GetValue(1));
                var activeConnections = Convert.ToInt32(reader.GetValue(2));
                var maxConnectionTime = Convert.ToInt32(reader.GetValue(3));
                
                // 获取更详细的连接池统计信息
                var stats = TCServer.Data.Repositories.DatabaseHelper.GetConnectionStats();
                
                // 只在连接数异常时记录警告
                if (totalConnections > 40 || activeConnections > 30 || stats.activeConnections > 8)
                {
                    _logger?.LogWarning(
                        "数据库连接池异常状态:\n" +
                        $"总连接数: {totalConnections}, 活动连接: {activeConnections}, " +
                        $"应用并行操作: {stats.activeConnections}/{stats.maxConcurrentConnections}");
                }
            }
        }
        catch (AuthenticationException ex) when (ex.Message.Contains("frame size") || ex.Message.Contains("corrupted frame"))
        {
            _logger?.LogWarning("数据库SSL连接错误，将在下次检查中重试: {Message}", ex.Message);
        }
        catch (MySql.Data.MySqlClient.MySqlException ex)
        {
            _logger?.LogWarning("数据库连接池状态检查MySQL错误: {ErrorCode} - {Message}", ex.Number, ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "检查数据库连接池状态失败: {Message}", ex.Message);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            // 停止数据库连接池监控
            _dbMonitorTimer?.Dispose();
            
            if (_host != null)
            {
                _logger?.LogInformation("正在关闭应用程序...");
                await _host.StopAsync();
                _logger?.LogInformation("应用程序关闭完成");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "关闭应用程序时发生错误");
        }
        finally
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }

    // 全局异常处理方法
    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var errorMessage = $"未处理的UI线程异常:\n{e.Exception.Message}";
            _logger?.LogError(e.Exception, "DispatcherUnhandledException: {Message}", e.Exception.Message);
            
            // 记录异常详情
            LogExceptionDetails(e.Exception, "DispatcherUnhandledException");
            
            // 显示用户友好的错误消息
            var result = MessageBox.Show(
                $"程序遇到了意外错误，建议重启程序。\n\n错误信息：{e.Exception.Message}\n\n是否继续运行程序？\n(选择'否'将关闭程序)",
                "程序错误",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);
            
            if (result == MessageBoxResult.No)
            {
                Shutdown(1);
            }
            else
            {
                e.Handled = true; // 继续运行
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"处理异常时发生错误：{ex.Message}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var exception = e.ExceptionObject as Exception;
            var errorMessage = exception?.Message ?? "未知错误";
            
            _logger?.LogError(exception, "UnhandledException: {Message}, IsTerminating: {IsTerminating}", 
                errorMessage, e.IsTerminating);
            
            // 记录异常详情
            LogExceptionDetails(exception, "UnhandledException");
            
            // 显示错误消息
            MessageBox.Show(
                $"程序遇到了严重错误，即将关闭。\n\n错误信息：{errorMessage}",
                "程序严重错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.WriteAllText($"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log", 
                    $"Critical error: {ex.Message}\nOriginal error: {e.ExceptionObject}");
            }
            catch { /* 忽略日志写入错误 */ }
        }
    }

    private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            _logger?.LogError(e.Exception, "UnobservedTaskException: {Message}", e.Exception.Message);
            
            // 记录异常详情
            LogExceptionDetails(e.Exception, "UnobservedTaskException");
            
            // 标记异常已被观察到，避免程序崩溃
            e.SetObserved();
            
            // 在UI线程上显示警告
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MessageBox.Show(
                    $"检测到后台任务异常，已自动处理。\n\n错误信息：{e.Exception.GetBaseException().Message}",
                    "后台任务异常",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }));
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.WriteAllText($"task_error_{DateTime.Now:yyyyMMdd_HHmmss}.log", 
                    $"Task exception handler error: {ex.Message}\nOriginal task error: {e.Exception}");
            }
            catch { /* 忽略日志写入错误 */ }
        }
    }

    private void LogExceptionDetails(Exception? exception, string context)
    {
        if (exception == null) return;
        
        try
        {
            var details = new System.Text.StringBuilder();
            details.AppendLine($"=== {context} 异常详情 ===");
            details.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            details.AppendLine($"异常类型: {exception.GetType().FullName}");
            details.AppendLine($"异常消息: {exception.Message}");
            
            if (exception.InnerException != null)
            {
                details.AppendLine($"内部异常: {exception.InnerException.GetType().FullName}");
                details.AppendLine($"内部异常消息: {exception.InnerException.Message}");
            }
            
            details.AppendLine("堆栈跟踪:");
            details.AppendLine(exception.StackTrace ?? "无堆栈跟踪信息");
            
            // 如果是数据库相关异常，记录额外信息
            if (exception is MySqlException mysqlEx)
            {
                details.AppendLine($"MySQL错误码: {mysqlEx.Number}");
                details.AppendLine($"SQL状态: {mysqlEx.SqlState}");
            }
            
            _logger?.LogError(details.ToString());
            
            // 写入紧急日志文件
            var emergencyLogPath = $"emergency_{DateTime.Now:yyyyMMdd}.log";
            System.IO.File.AppendAllText(emergencyLogPath, details.ToString() + Environment.NewLine);
        }
        catch
        {
            // 忽略日志记录错误，避免无限递归
        }
    }
} 