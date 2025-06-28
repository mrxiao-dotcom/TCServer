using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TCServer.Common.Models;
using TCServer.Common.Interfaces;
using TCServer.Core.Services;
using System.Threading;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TCServer
{
    public partial class AccountManagementWindow : Window
    {
        private readonly IAccountRepository _accountRepository;
        private readonly ILogger<AccountManagementWindow> _logger;
        private List<AccountInfo> _accounts = new();
        private AccountDataService? _accountDataService;
        private Timer? _uiRefreshTimer;
        private List<AccountBalance> _accountBalances = new();
        private bool _isDisposed = false;
        
        // 图表相关字段
        private AccountInfo? _selectedAccountForChart;
        private List<AccountEquityHistory> _equityHistory = new();

        public AccountManagementWindow()
        {
            InitializeComponent();
            
            // 获取依赖注入的服务
            var host = (App.Current as App)?.Host;
            if (host != null)
            {
                _accountRepository = host.Services.GetRequiredService<IAccountRepository>();
                _logger = host.Services.GetRequiredService<ILogger<AccountManagementWindow>>();
                
                // 预先创建AccountDataService，避免重复创建
                try
                {
                    var binanceApiService = host.Services.GetRequiredService<BinanceApiService>();
                    var accountDataLogger = host.Services.GetRequiredService<ILogger<AccountDataService>>();
                    var notificationService = host.Services.GetRequiredService<NotificationService>();
                    _accountDataService = new AccountDataService(binanceApiService, _accountRepository, accountDataLogger, notificationService);
                    LogMessage("✅ 账户数据服务初始化成功");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "初始化账户数据服务失败");
                    LogMessage($"❌ 账户数据服务初始化失败: {ex.Message}");
                }
            }
            else
            {
                throw new InvalidOperationException("无法获取应用程序Host");
            }

            // 窗口加载时刷新数据
            Loaded += AccountManagementWindow_Loaded;
        }

        private async void AccountManagementWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshAccountsAsync();
            await LoadAllAccountBalancesAsync();
        }

        private async void btnAddAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LogMessage("正在打开添加账户窗口...");
                
                var addAccountWindow = new AccountEditWindow();
                if (addAccountWindow.ShowDialog() == true)
                {
                    var newAccount = addAccountWindow.AccountInfo;
                    if (newAccount != null)
                    {
                        LogMessage($"正在添加账户: {newAccount.AcctName}");
                        
                        var result = await _accountRepository.AddAccountAsync(newAccount);
                        if (result)
                        {
                            LogMessage($"账户添加成功: {newAccount.AcctName}");
                            await RefreshAccountsAsync();
                        }
                        else
                        {
                            LogMessage($"账户添加失败: {newAccount.AcctName}");
                            MessageBox.Show("添加账户失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加账户时发生错误");
                LogMessage($"添加账户时发生错误: {ex.Message}");
                MessageBox.Show($"添加账户时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void btnEditAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedAccount = dgAccounts.SelectedItem as AccountInfo;
                if (selectedAccount == null)
                {
                    MessageBox.Show("请选择要修改的账户", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                LogMessage($"正在编辑账户: {selectedAccount.AcctName}");
                
                var editAccountWindow = new AccountEditWindow(selectedAccount);
                if (editAccountWindow.ShowDialog() == true)
                {
                    var updatedAccount = editAccountWindow.AccountInfo;
                    if (updatedAccount != null)
                    {
                        LogMessage($"正在更新账户: {updatedAccount.AcctName}");
                        
                        var result = await _accountRepository.UpdateAccountAsync(updatedAccount);
                        if (result)
                        {
                            LogMessage($"账户更新成功: {updatedAccount.AcctName}");
                            await RefreshAccountsAsync();
                        }
                        else
                        {
                            LogMessage($"账户更新失败: {updatedAccount.AcctName}");
                            MessageBox.Show("更新账户失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "修改账户时发生错误");
                LogMessage($"修改账户时发生错误: {ex.Message}");
                MessageBox.Show($"修改账户时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void btnDeleteAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedAccount = dgAccounts.SelectedItem as AccountInfo;
                if (selectedAccount == null)
                {
                    MessageBox.Show("请选择要删除的账户", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var result = MessageBox.Show(
                    $"确定要删除账户 '{selectedAccount.AcctName}' 吗？\n\n此操作将同时删除该账户的所有余额和持仓记录，且无法恢复！", 
                    "确认删除", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    LogMessage($"正在删除账户: {selectedAccount.AcctName}");
                    
                    var deleteResult = await _accountRepository.DeleteAccountAsync(selectedAccount.AcctId);
                    if (deleteResult)
                    {
                        LogMessage($"账户删除成功: {selectedAccount.AcctName}");
                        await RefreshAccountsAsync();
                        
                        // 清空持仓显示
                        ClearPositionDisplay();
                    }
                    else
                    {
                        LogMessage($"账户删除失败: {selectedAccount.AcctName}");
                        MessageBox.Show("删除账户失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除账户时发生错误");
                LogMessage($"删除账户时发生错误: {ex.Message}");
                MessageBox.Show($"删除账户时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAccountsAsync();
        }

        private async void btnStartQuery_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_accountDataService == null)
                {
                    LogMessage("❌ 账户数据服务未初始化，请重新打开窗口");
                    MessageBox.Show("账户数据服务未初始化，请关闭窗口后重新打开", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 禁用按钮防止重复点击
                btnStartQuery.IsEnabled = false;

                if (_accountDataService.IsRunning)
                {
                    // 停止查询
                    LogMessage("=== 正在停止账户信息定时查询服务 ===");
                    
                    await Task.Run(() => _accountDataService.StopQuery());
                    
                    _uiRefreshTimer?.Dispose();
                    _uiRefreshTimer = null;
                    
                    btnStartQuery.Content = "启动账户信息查询";
                    btnStartQuery.Background = System.Windows.Media.Brushes.Purple;
                    LogMessage("❌ 账户信息定时查询已停止");
                    LogMessage($"⏰ 停止时间：{DateTime.Now:HH:mm:ss}");
                }
                else
                {
                    // 检查是否有账户配置
                    LogMessage("🔍 正在检查账户配置...");
                    var accounts = await _accountRepository.GetAllAccountsAsync();
                    if (accounts == null || accounts.Count == 0)
                    {
                        LogMessage("⚠️ 数据库中没有配置任何账户");
                        LogMessage("💡 请先添加账户信息后再启动服务");
                        MessageBox.Show("请先添加账户信息后再启动服务", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    
                    LogMessage($"📋 检测到 {accounts.Count} 个已配置账户");
                    
                    // 启动查询
                    LogMessage("=== 正在启动账户信息定时查询服务 ===");
                    
                    await Task.Run(() => _accountDataService.StartQuery());
                    
                    // 启动UI刷新定时器，每30秒刷新一次界面数据
                    _uiRefreshTimer = new Timer(async _ => 
                    {
                        try
                        {
                            // 确保在UI线程中执行所有UI操作
                            await Dispatcher.InvokeAsync(() =>
                            {
                                LogMessage("🔄 UI刷新定时器触发，刷新界面数据...");
                            });
                            
                            await RefreshEquityDisplayAsync();
                            
                            await Dispatcher.InvokeAsync(() =>
                            {
                                LogMessage("✅ UI界面数据刷新完成");
                            });
                        }
                        catch (Exception ex)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                LogMessage($"❌ UI刷新定时器执行出错: {ex.Message}");
                            });
                        }
                    }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                    
                    btnStartQuery.Content = "停止查询";
                    btnStartQuery.Background = System.Windows.Media.Brushes.Red;
                    LogMessage("✅ 账户信息定时查询已启动！");
                    LogMessage($"📊 API查询间隔：30秒 (调试模式)");
                    LogMessage($"🖥️ UI刷新间隔：30秒");
                    LogMessage($"⏰ 当前时间：{DateTime.Now:HH:mm:ss}");
                    LogMessage("🔍 请观察日志，30秒后应该看到定时器触发的消息");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动/停止账户信息查询时发生错误");
                LogMessage($"启动/停止账户信息查询时发生错误: {ex.Message}");
                MessageBox.Show($"操作失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 重新启用按钮
                btnStartQuery.IsEnabled = true;
            }
        }

        private async void dgAccounts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedAccount = dgAccounts.SelectedItem as AccountInfo;
            if (selectedAccount != null)
            {
                await LoadAccountPositions(selectedAccount);
                
                // 更新图表选择的账户并加载净值走势
                _selectedAccountForChart = selectedAccount;
                await LoadAndDisplayChart(selectedAccount);
            }
            else
            {
                ClearPositionDisplay();
                ClearChart();
            }
        }

        private async void dgAccountBalances_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedBalance = dgAccountBalances.SelectedItem as AccountBalance;
            if (selectedBalance != null)
            {
                // 根据选中的余额记录查找对应的账户，并加载持仓
                var account = _accounts.FirstOrDefault(a => a.AcctId == selectedBalance.AccountId);
                if (account != null)
                {
                    await LoadAccountPositions(account);
                    
                    // 同步选择账户列表中对应的账户
                    dgAccounts.SelectedItem = account;
                    
                    // 更新图表选择的账户并加载净值走势
                    _selectedAccountForChart = account;
                    await LoadAndDisplayChart(account);
                }
            }
        }

        private async Task RefreshAccountsAsync()
        {
            try
            {
                LogMessage("正在刷新账户列表...");
                
                _accounts = await _accountRepository.GetAllAccountsAsync();
                dgAccounts.ItemsSource = _accounts;
                
                LogMessage($"账户列表刷新完成，共 {_accounts.Count} 个账户");
                
                // 刷新账户余额列表
                await LoadAllAccountBalancesAsync();
                
                // 如果有选中的账户，重新加载其信息
                var selectedAccount = dgAccounts.SelectedItem as AccountInfo;
                if (selectedAccount != null)
                {
                    await LoadAccountPositions(selectedAccount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新账户列表时发生错误");
                LogMessage($"刷新账户列表时发生错误: {ex.Message}");
                MessageBox.Show($"刷新账户列表时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadAccountPositions(AccountInfo account)
        {
            try
            {
                LogMessage($"正在加载账户 {account.AcctName} 的持仓信息...");

                // 加载持仓信息
                var positions = await _accountRepository.GetAccountPositionsAsync(account.AcctId);
                dgPositions.ItemsSource = positions.Where(p => Math.Abs(p.PositionAmt) > 0).ToList();
                
                LogMessage($"账户 {account.AcctName} 持仓信息加载完成，共 {positions.Count} 个持仓");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载账户持仓信息时发生错误");
                LogMessage($"加载账户持仓信息时发生错误: {ex.Message}");
                ClearPositionDisplay();
            }
        }

        private void ClearPositionDisplay()
        {
            dgPositions.ItemsSource = null;
        }

        /// <summary>
        /// 加载所有账户的实时余额
        /// </summary>
        private async Task LoadAllAccountBalancesAsync()
        {
            try
            {
                LogMessage("正在加载所有账户实时余额...");
                
                _accountBalances = await _accountRepository.GetAllAccountRealTimeBalancesAsync();
                dgAccountBalances.ItemsSource = _accountBalances;
                
                LogMessage($"实时余额加载完成，共 {_accountBalances.Count} 个账户");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载账户实时余额时发生错误");
                LogMessage($"加载实时余额时发生错误: {ex.Message}");
                dgAccountBalances.ItemsSource = null;
            }
        }

        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                txtLog.AppendText($"[{timestamp}] {message}\n");
                txtLog.ScrollToEnd();
            });
        }

        /// <summary>
        /// 刷新所有账户余额显示
        /// </summary>
        private async Task RefreshEquityDisplayAsync()
        {
            try
            {
                // 在UI线程中执行
                await Dispatcher.InvokeAsync(async () =>
                {
                    // 刷新所有账户余额列表
                    await LoadAllAccountBalancesAsync();
                    
                    // 如果有选中的账户，刷新其持仓信息
                    var selectedAccount = dgAccounts.SelectedItem as AccountInfo;
                    if (selectedAccount != null)
                    {
                        var positions = await _accountRepository.GetAccountPositionsAsync(selectedAccount.AcctId);
                        dgPositions.ItemsSource = positions.Where(p => Math.Abs(p.PositionAmt) > 0).ToList();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新账户余额显示时发生错误");
            }
        }

        /// <summary>
        /// 窗口关闭时清理资源
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                LogMessage("🔄 正在关闭账户管理窗口，清理资源...");

                // 停止账户数据服务
                if (_accountDataService != null)
                {
                    try
                    {
                        if (_accountDataService.IsRunning)
                        {
                            _accountDataService.StopQuery();
                            LogMessage("✅ 账户数据服务已停止");
                        }
                        _accountDataService.Dispose();
                        _accountDataService = null;
                        LogMessage("✅ 账户数据服务已释放");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "停止账户数据服务时发生错误");
                        LogMessage($"❌ 停止账户数据服务时发生错误: {ex.Message}");
                    }
                }

                // 停止UI刷新定时器
                if (_uiRefreshTimer != null)
                {
                    try
                    {
                        _uiRefreshTimer.Dispose();
                        _uiRefreshTimer = null;
                        LogMessage("✅ UI刷新定时器已停止");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "停止UI刷新定时器时发生错误");
                        LogMessage($"❌ 停止UI刷新定时器时发生错误: {ex.Message}");
                    }
                }

                LogMessage("✅ 账户管理窗口资源清理完成");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "清理资源时发生错误");
                // 即便清理时出错，也要继续关闭窗口
            }
            finally
            {
                try
                {
                    base.OnClosed(e);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "调用基类OnClosed时发生错误");
                }
            }
        }

        /// <summary>
        /// 窗口关闭前的确认
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                if (_accountDataService?.IsRunning == true)
                {
                    var result = MessageBox.Show(
                        "账户数据服务正在运行中，关闭窗口将停止所有服务。确定要关闭吗？",
                        "确认关闭",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.No)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "窗口关闭确认时发生错误");
            }
            finally
            {
                try
                {
                    base.OnClosing(e);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "调用基类OnClosing时发生错误");
                }
            }
        }

        #region 图表相关方法

        /// <summary>
        /// 时间范围选择变化事件
        /// </summary>
        private async void cmbTimeRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedAccountForChart != null)
            {
                await LoadAndDisplayChart(_selectedAccountForChart);
            }
        }

        /// <summary>
        /// 刷新图表按钮点击事件
        /// </summary>
        private async void btnRefreshChart_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAccountForChart != null)
            {
                await LoadAndDisplayChart(_selectedAccountForChart);
                LogMessage($"已刷新 {_selectedAccountForChart.AcctName} 的净值走势图");
            }
            else
            {
                LogMessage("请先选择一个账户以查看净值走势");
            }
        }

        /// <summary>
        /// 加载并显示账户净值走势图
        /// </summary>
        private async Task LoadAndDisplayChart(AccountInfo account)
        {
            try
            {
                // 获取时间范围
                var days = GetSelectedDays();
                var endDate = DateTime.Now;
                var startDate = endDate.AddDays(-days);

                LogMessage($"正在加载 {account.AcctName} 近{days}天的净值数据...");
                
                // 从数据库获取权益历史数据
                _equityHistory = await _accountRepository.GetAccountEquityHistoryAsync(
                    account.AcctId.ToString(), startDate, endDate);

                if (_equityHistory == null || _equityHistory.Count == 0)
                {
                    LogMessage($"暂无 {account.AcctName} 的历史净值数据");
                    ClearChart();
                    return;
                }

                // 按时间排序
                _equityHistory = _equityHistory.OrderBy(h => h.CreateTime).ToList();
                
                LogMessage($"已加载 {account.AcctName} {_equityHistory.Count} 条净值记录");
                
                // 绘制图表
                DrawEquityChart();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载净值走势图时发生错误");
                LogMessage($"加载净值走势图时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取选择的天数
        /// </summary>
        private int GetSelectedDays()
        {
            return cmbTimeRange.SelectedIndex switch
            {
                1 => 15,
                2 => 30,
                _ => 7
            };
        }

        /// <summary>
        /// 绘制权益走势图
        /// </summary>
        private void DrawEquityChart()
        {
            if (_equityHistory == null || _equityHistory.Count == 0)
            {
                ClearChart();
                return;
            }

            try
            {
                // 清空画布
                chartCanvas.Children.Clear();

                // 获取画布尺寸
                var canvasWidth = chartCanvas.ActualWidth;
                var canvasHeight = chartCanvas.ActualHeight;
                
                if (canvasWidth <= 0 || canvasHeight <= 0)
                {
                    // 如果画布尺寸还没有确定，延迟绘制
                    chartCanvas.SizeChanged += (s, e) => DrawEquityChart();
                    return;
                }

                // 计算边距
                var marginLeft = 60;
                var marginRight = 20;
                var marginTop = 20;
                var marginBottom = 40;
                
                var chartWidth = canvasWidth - marginLeft - marginRight;
                var chartHeight = canvasHeight - marginTop - marginBottom;

                if (chartWidth <= 0 || chartHeight <= 0) return;

                // 获取数据范围
                var minEquity = _equityHistory.Min(h => h.Equity);
                var maxEquity = _equityHistory.Max(h => h.Equity);
                var equityRange = maxEquity - minEquity;
                
                if (equityRange == 0) equityRange = maxEquity * 0.1m; // 避免除以0

                // 绘制背景网格
                DrawGrid(marginLeft, marginTop, chartWidth, chartHeight);

                // 绘制Y轴标签
                DrawYAxisLabels(marginLeft, marginTop, chartHeight, minEquity, maxEquity);

                // 绘制X轴标签
                DrawXAxisLabels(marginLeft, marginTop + chartHeight, chartWidth);

                // 绘制数据线
                DrawDataLine(marginLeft, marginTop, chartWidth, chartHeight, minEquity, equityRange);
                
                LogMessage($"净值走势图绘制完成，数据点: {_equityHistory.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "绘制净值走势图时发生错误");
                LogMessage($"绘制净值走势图时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 绘制背景网格
        /// </summary>
        private void DrawGrid(double marginLeft, double marginTop, double chartWidth, double chartHeight)
        {
            var gridBrush = new SolidColorBrush(Color.FromRgb(230, 230, 230));
            
            // 绘制水平网格线
            for (int i = 0; i <= 5; i++)
            {
                var y = marginTop + (chartHeight / 5) * i;
                var line = new Line
                {
                    X1 = marginLeft,
                    Y1 = y,
                    X2 = marginLeft + chartWidth,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };
                chartCanvas.Children.Add(line);
            }

            // 绘制垂直网格线
            for (int i = 0; i <= 6; i++)
            {
                var x = marginLeft + (chartWidth / 6) * i;
                var line = new Line
                {
                    X1 = x,
                    Y1 = marginTop,
                    X2 = x,
                    Y2 = marginTop + chartHeight,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };
                chartCanvas.Children.Add(line);
            }
        }

        /// <summary>
        /// 绘制Y轴标签
        /// </summary>
        private void DrawYAxisLabels(double marginLeft, double marginTop, double chartHeight, decimal minEquity, decimal maxEquity)
        {
            for (int i = 0; i <= 5; i++)
            {
                var y = marginTop + (chartHeight / 5) * (5 - i);
                var value = minEquity + (maxEquity - minEquity) * i / 5;
                
                var textBlock = new TextBlock
                {
                    Text = $"{value:F0}",
                    FontSize = 10,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                
                Canvas.SetLeft(textBlock, marginLeft - 50);
                Canvas.SetTop(textBlock, y - 7);
                chartCanvas.Children.Add(textBlock);
            }
        }

        /// <summary>
        /// 绘制X轴标签
        /// </summary>
        private void DrawXAxisLabels(double marginLeft, double baselineY, double chartWidth)
        {
            if (_equityHistory.Count == 0) return;
            
            var startTime = _equityHistory.First().CreateTime;
            var endTime = _equityHistory.Last().CreateTime;
            var timeSpan = endTime - startTime;
            
            for (int i = 0; i <= 6; i++)
            {
                var x = marginLeft + (chartWidth / 6) * i;
                var time = startTime.AddTicks(timeSpan.Ticks * i / 6);
                
                var textBlock = new TextBlock
                {
                    Text = time.ToString("MM/dd"),
                    FontSize = 10,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                
                Canvas.SetLeft(textBlock, x - 15);
                Canvas.SetTop(textBlock, baselineY + 5);
                chartCanvas.Children.Add(textBlock);
            }
        }

        /// <summary>
        /// 绘制数据线
        /// </summary>
        private void DrawDataLine(double marginLeft, double marginTop, double chartWidth, double chartHeight, decimal minEquity, decimal equityRange)
        {
            if (_equityHistory.Count < 2) return;

            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 123, 255)),
                StrokeThickness = 2,
                Fill = null
            };

            var startTime = _equityHistory.First().CreateTime;
            var endTime = _equityHistory.Last().CreateTime;
            var timeSpan = endTime - startTime;

            foreach (var point in _equityHistory)
            {
                var timeRatio = timeSpan.TotalMinutes > 0 ? (point.CreateTime - startTime).TotalMinutes / timeSpan.TotalMinutes : 0;
                var x = marginLeft + chartWidth * timeRatio;
                
                var equityRatio = equityRange > 0 ? (double)((point.Equity - minEquity) / equityRange) : 0.5;
                var y = marginTop + chartHeight * (1 - equityRatio);
                
                polyline.Points.Add(new Point(x, y));
            }

            chartCanvas.Children.Add(polyline);

            // 绘制数据点
            foreach (var point in _equityHistory)
            {
                var timeRatio = timeSpan.TotalMinutes > 0 ? (point.CreateTime - startTime).TotalMinutes / timeSpan.TotalMinutes : 0;
                var x = marginLeft + chartWidth * timeRatio;
                
                var equityRatio = equityRange > 0 ? (double)((point.Equity - minEquity) / equityRange) : 0.5;
                var y = marginTop + chartHeight * (1 - equityRatio);
                
                var ellipse = new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = new SolidColorBrush(Color.FromRgb(0, 123, 255))
                };
                
                Canvas.SetLeft(ellipse, x - 2);
                Canvas.SetTop(ellipse, y - 2);
                chartCanvas.Children.Add(ellipse);
            }
        }

        /// <summary>
        /// 清空图表
        /// </summary>
        private void ClearChart()
        {
            chartCanvas.Children.Clear();
            
            // 显示无数据提示
            var textBlock = new TextBlock
            {
                Text = "暂无数据",
                FontSize = 14,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            Canvas.SetLeft(textBlock, (chartCanvas.ActualWidth - 60) / 2);
            Canvas.SetTop(textBlock, (chartCanvas.ActualHeight - 20) / 2);
            chartCanvas.Children.Add(textBlock);
        }

        #endregion
    }
} 