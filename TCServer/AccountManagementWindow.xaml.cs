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
            }
            else
            {
                ClearPositionDisplay();
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
    }
} 