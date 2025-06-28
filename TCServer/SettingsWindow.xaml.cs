using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCServer.Common.Interfaces;
using TCServer.Common.Models;
using TCServer.Core.Services;


namespace TCServer;

public partial class SettingsWindow : Window
{
    private readonly ISystemConfigRepository _configRepository;
    private readonly ILogger<SettingsWindow> _logger;
    private readonly string _settingsFilePath = "settings.json";
    private readonly HttpClient _httpClient;
    private readonly NotificationService _notificationService;
    private readonly IAccountRepository _accountRepository;
    
    // 添加突破提醒设置属性
    public BreakthroughSettings BreakthroughSettings { get; private set; } = new BreakthroughSettings();
    
    // 添加推送设置属性
    public NotificationSettings NotificationSettings { get; private set; } = new NotificationSettings();

    public SettingsWindow()
    {
        InitializeComponent();

        var host = ((App)Application.Current).Host;
        _configRepository = host.Services.GetRequiredService<ISystemConfigRepository>();
        _logger = host.Services.GetRequiredService<ILogger<SettingsWindow>>();
        _notificationService = host.Services.GetRequiredService<NotificationService>();
        _accountRepository = host.Services.GetRequiredService<IAccountRepository>();
        _httpClient = new HttpClient();

        Loaded += SettingsWindow_Loaded;
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 加载系统配置
            var fetchTimeConfig = await _configRepository.GetConfigAsync("KlineFetchTime");
            var batchSizeConfig = await _configRepository.GetConfigAsync("BatchSize");
            var apiKeyConfig = await _configRepository.GetConfigAsync("BinanceApiKey");
            var apiSecretConfig = await _configRepository.GetConfigAsync("BinanceApiSecret");

            txtFetchTime.Text = fetchTimeConfig?.Value ?? "00:05:00";
            txtBatchSize.Text = batchSizeConfig?.Value ?? "10";
            txtApiKey.Password = apiKeyConfig?.Value ?? "";
            txtApiSecret.Password = apiSecretConfig?.Value ?? "";
            
            // 加载突破提醒设置
            LoadSettings();
            
            // 加载推送设置
            LoadPushSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载设置时发生错误");
            MessageBox.Show($"加载设置时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void btnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 验证系统设置
            if (!TimeSpan.TryParse(txtFetchTime.Text, out _))
            {
                MessageBox.Show("请输入正确的时间格式（HH:mm:ss）", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!int.TryParse(txtBatchSize.Text, out var batchSize) || batchSize <= 0)
            {
                MessageBox.Show("请输入正确的批次大小（大于0的整数）", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 保存系统配置
            var now = DateTime.Now;
            var fetchTimeConfig = new TCServer.Common.Models.SystemConfig
            {
                Key = "KlineFetchTime",
                Value = txtFetchTime.Text,
                Description = "K线数据获取时间",
                CreatedAt = now,
                UpdatedAt = now
            };
            
            var batchSizeConfig = new TCServer.Common.Models.SystemConfig
            {
                Key = "BatchSize",
                Value = txtBatchSize.Text,
                Description = "每批次获取的交易对数量",
                CreatedAt = now,
                UpdatedAt = now
            };

            var apiKeyConfig = new TCServer.Common.Models.SystemConfig
            {
                Key = "BinanceApiKey",
                Value = txtApiKey.Password,
                Description = "币安API密钥",
                CreatedAt = now,
                UpdatedAt = now
            };

            var apiSecretConfig = new TCServer.Common.Models.SystemConfig
            {
                Key = "BinanceApiSecret",
                Value = txtApiSecret.Password,
                Description = "币安API密钥Secret",
                CreatedAt = now,
                UpdatedAt = now
            };
            
            await _configRepository.SaveConfigAsync(fetchTimeConfig);
            await _configRepository.SaveConfigAsync(batchSizeConfig);
            await _configRepository.SaveConfigAsync(apiKeyConfig);
            await _configRepository.SaveConfigAsync(apiSecretConfig);
            
            // 保存突破提醒设置
            SaveSettings();
            
            // 保存推送设置
            SavePushSettings();

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置时发生错误");
            MessageBox.Show($"保存设置时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void btnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // 加载设置
    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                
                if (settings != null && settings.BreakthroughSettings != null)
                {
                    BreakthroughSettings = settings.BreakthroughSettings;
                    
                    // 填充UI控件
                    txtTokens.Text = string.Join(Environment.NewLine, BreakthroughSettings.Tokens ?? new List<string>());
                    
                    txtThreshold1.Text = BreakthroughSettings.Threshold1.ToString();
                    txtThreshold2.Text = BreakthroughSettings.Threshold2.ToString();
                    txtThreshold3.Text = BreakthroughSettings.Threshold3.ToString();
                    
                    chkThreshold1Enabled.IsChecked = BreakthroughSettings.Threshold1Enabled;
                    chkThreshold2Enabled.IsChecked = BreakthroughSettings.Threshold2Enabled;
                    chkThreshold3Enabled.IsChecked = BreakthroughSettings.Threshold3Enabled;
                    
                    chkEnableNotifications.IsChecked = BreakthroughSettings.EnableNotifications;
                    
                    // 加载新高/新低突破设置
                    chkEnableHighLowBreakthrough.IsChecked = BreakthroughSettings.EnableHighLowBreakthrough;
                    chkHighLowDays1Enabled.IsChecked = BreakthroughSettings.HighLowDays1Enabled;
                    chkHighLowDays2Enabled.IsChecked = BreakthroughSettings.HighLowDays2Enabled;
                    chkHighLowDays3Enabled.IsChecked = BreakthroughSettings.HighLowDays3Enabled;
                }
                else
                {
                    CreateDefaultSettings();
                }
            }
            else
            {
                CreateDefaultSettings();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载设置时出错: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            CreateDefaultSettings();
        }
    }
    
    // 创建默认设置
    private void CreateDefaultSettings()
    {
        BreakthroughSettings = new BreakthroughSettings
        {
            Tokens = new List<string>(),
            Threshold1 = 5,
            Threshold2 = 10,
            Threshold3 = 20,
            Threshold1Enabled = true,
            Threshold2Enabled = true,
            Threshold3Enabled = true,
            EnableNotifications = true,
            EnableHighLowBreakthrough = true,
            HighLowDays1Enabled = true,
            HighLowDays2Enabled = true,
            HighLowDays3Enabled = true,
            HighLowDays1 = 5,
            HighLowDays2 = 10,
            HighLowDays3 = 20
        };
    }

    // 保存设置到文件
    private void SaveSettings()
    {
        try
        {
            // 从UI控件获取设置值
            BreakthroughSettings.Tokens = txtTokens.Text
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            
            if (!decimal.TryParse(txtThreshold1.Text, out decimal threshold1))
                threshold1 = 5;
            if (!decimal.TryParse(txtThreshold2.Text, out decimal threshold2))
                threshold2 = 10;
            if (!decimal.TryParse(txtThreshold3.Text, out decimal threshold3))
                threshold3 = 20;
            
            BreakthroughSettings.Threshold1 = threshold1;
            BreakthroughSettings.Threshold2 = threshold2;
            BreakthroughSettings.Threshold3 = threshold3;
            
            BreakthroughSettings.Threshold1Enabled = chkThreshold1Enabled.IsChecked ?? true;
            BreakthroughSettings.Threshold2Enabled = chkThreshold2Enabled.IsChecked ?? true;
            BreakthroughSettings.Threshold3Enabled = chkThreshold3Enabled.IsChecked ?? true;
            
            BreakthroughSettings.EnableNotifications = chkEnableNotifications.IsChecked ?? true;
            
            // 保存新高/新低突破设置
            BreakthroughSettings.EnableHighLowBreakthrough = chkEnableHighLowBreakthrough.IsChecked ?? true;
            BreakthroughSettings.HighLowDays1Enabled = chkHighLowDays1Enabled.IsChecked ?? true;
            BreakthroughSettings.HighLowDays2Enabled = chkHighLowDays2Enabled.IsChecked ?? true;
            BreakthroughSettings.HighLowDays3Enabled = chkHighLowDays3Enabled.IsChecked ?? true;
            
            // 创建或获取应用设置
            AppSettings appSettings;
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                appSettings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                appSettings = new AppSettings();
            }
            
            // 更新突破提醒设置
            appSettings.BreakthroughSettings = BreakthroughSettings;
            
            // 保存设置到文件
            var updatedJson = JsonConvert.SerializeObject(appSettings, Formatting.Indented);
            File.WriteAllText(_settingsFilePath, updatedJson);
            
            LogMessage("设置已保存");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存设置时出错: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 测试推送逻辑
    private async void btnTestToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            btnTestToken.IsEnabled = false;
            
            var tokens = txtTokens.Text
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            
            if (tokens.Count == 0)
            {
                MessageBox.Show("请先输入至少一个Token", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            LogMessage("开始测试推送...");
            
            foreach (var token in tokens)
            {
                try
                {
                    var testResult = await SendTestNotification(token);
                    LogMessage($"Token [{token}] 测试结果: {testResult}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Token [{token}] 测试失败: {ex.Message}");
                }
            }
            
            LogMessage("测试完成");
        }
        catch (Exception ex)
        {
            LogMessage($"测试过程中出错: {ex.Message}");
            MessageBox.Show($"测试中发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btnTestToken.IsEnabled = true;
        }
    }
    
    // 发送测试通知
    private async Task<string> SendTestNotification(string token)
    {
        var url = $"https://wx.xtuis.cn/{token}.send";
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("text", "行情突破提醒测试"),
            new KeyValuePair<string, string>("desp", $"这是一条测试消息，发送时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
        });
        
        var response = await _httpClient.PostAsync(url, content);
        var responseContent = await response.Content.ReadAsStringAsync();
        
        return $"HTTP {(int)response.StatusCode} {response.StatusCode}: {responseContent}";
    }
    
    // 记录日志消息
    private void LogMessage(string message)
    {
        Dispatcher.Invoke(() =>
        {
            txtPushLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            txtPushLog.ScrollToEnd();
        });
    }

    // === 新增推送相关方法 ===
    
    /// <summary>
    /// 测试推送按钮点击事件
    /// </summary>
    private async void btnTestPush_Click(object sender, RoutedEventArgs e)
    {
        var token = txtPushToken.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show("请先输入虾推啥Token", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var success = await _notificationService.TestXtuisTokenAsync(token);
            if (success)
            {
                MessageBox.Show("测试推送成功！请检查您的微信是否收到测试消息。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("测试推送失败，请检查Token是否正确。", "失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "测试推送时发生异常");
            MessageBox.Show($"测试推送时发生异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 预览推送消息按钮点击事件
    /// </summary>
    private async void btnPreviewPush_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 获取账户余额数据
            var balances = await _accountRepository.GetAllAccountRealTimeBalancesAsync();
            var balanceData = balances.Select(b => (
                AccountName: b.AccountName ?? "未知账户",
                TotalEquity: b.TotalEquity,
                UnrealizedPnl: b.UnrealizedPnl,
                UpdateTime: b.Timestamp
            )).ToList();

            // 格式化推送消息
            var message = _notificationService.FormatAccountBalancesMessage(balanceData);
            txtPushPreview.Text = message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "预览推送消息时发生异常");
            txtPushPreview.Text = $"预览失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 加载推送设置到界面
    /// </summary>
    private void LoadPushSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var appSettings = JsonConvert.DeserializeObject<AppSettings>(json);
                if (appSettings?.NotificationSettings != null)
                {
                    NotificationSettings = appSettings.NotificationSettings;
                    
                    // 加载基本设置
                    chkEnablePush.IsChecked = NotificationSettings.IsEnabled;
                    txtPushToken.Text = NotificationSettings.XtuisToken;
                    
                    // 加载时间段设置
                    LoadTimeSlotSettings();
                    
                    _logger.LogInformation("推送设置已从文件加载");
                }
                else
                {
                    _logger.LogInformation("配置文件中未找到推送设置，使用默认设置");
                    // 初始化默认的推送设置
                    NotificationSettings = new NotificationSettings();
                }
            }
            else
            {
                _logger.LogInformation("配置文件不存在，使用默认推送设置");
                // 初始化默认的推送设置
                NotificationSettings = new NotificationSettings();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载推送设置时发生异常");
            MessageBox.Show($"加载推送设置时出错: {ex.Message}\n使用默认设置", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            // 初始化默认的推送设置
            NotificationSettings = new NotificationSettings();
        }
    }

    /// <summary>
    /// 加载时间段设置
    /// </summary>
    private void LoadTimeSlotSettings()
    {
        // 全天时段 (0-23)
        var morningSlot = NotificationSettings.PushTimeSlots.FirstOrDefault(s => s.StartHour == 0 && s.EndHour == 23);
        if (morningSlot != null)
        {
            chkMorningEnabled.IsChecked = morningSlot.IsEnabled;
            chkMorning00.IsChecked = morningSlot.PushMinutes.Contains(0);
            chkMorning10.IsChecked = morningSlot.PushMinutes.Contains(10);
            chkMorning20.IsChecked = morningSlot.PushMinutes.Contains(20);
            chkMorning30.IsChecked = morningSlot.PushMinutes.Contains(30);
            chkMorning40.IsChecked = morningSlot.PushMinutes.Contains(40);
            chkMorning50.IsChecked = morningSlot.PushMinutes.Contains(50);
        }

        // 白天工作时段 (6-17)
        var afternoonSlot = NotificationSettings.PushTimeSlots.FirstOrDefault(s => s.StartHour == 6 && s.EndHour == 17);
        if (afternoonSlot != null)
        {
            chkAfternoonEnabled.IsChecked = afternoonSlot.IsEnabled;
            chkAfternoon00.IsChecked = afternoonSlot.PushMinutes.Contains(0);
            chkAfternoon10.IsChecked = afternoonSlot.PushMinutes.Contains(10);
            chkAfternoon20.IsChecked = afternoonSlot.PushMinutes.Contains(20);
            chkAfternoon30.IsChecked = afternoonSlot.PushMinutes.Contains(30);
            chkAfternoon40.IsChecked = afternoonSlot.PushMinutes.Contains(40);
            chkAfternoon50.IsChecked = afternoonSlot.PushMinutes.Contains(50);
        }

        // 晚间时段 (18-23)
        var eveningSlot = NotificationSettings.PushTimeSlots.FirstOrDefault(s => s.StartHour == 18 && s.EndHour == 23);
        if (eveningSlot != null)
        {
            chkEveningEnabled.IsChecked = eveningSlot.IsEnabled;
            chkEvening00.IsChecked = eveningSlot.PushMinutes.Contains(0);
            chkEvening10.IsChecked = eveningSlot.PushMinutes.Contains(10);
            chkEvening20.IsChecked = eveningSlot.PushMinutes.Contains(20);
            chkEvening30.IsChecked = eveningSlot.PushMinutes.Contains(30);
            chkEvening40.IsChecked = eveningSlot.PushMinutes.Contains(40);
            chkEvening50.IsChecked = eveningSlot.PushMinutes.Contains(50);
        }
    }

    /// <summary>
    /// 保存推送设置
    /// </summary>
    private void SavePushSettings()
    {
        try
        {
            NotificationSettings.IsEnabled = chkEnablePush.IsChecked ?? false;
            NotificationSettings.XtuisToken = txtPushToken.Text.Trim();

            // 清空现有时间段设置
            NotificationSettings.PushTimeSlots.Clear();

            // 保存早间时段
            if (chkMorningEnabled.IsChecked ?? false)
            {
                var morningSlot = new PushTimeSlot
                {
                    StartHour = 0,  // 全天：0-23点
                    EndHour = 23,
                    IsEnabled = true,
                    PushMinutes = new List<int>()
                };

                if (chkMorning00.IsChecked ?? false) morningSlot.PushMinutes.Add(0);
                if (chkMorning10.IsChecked ?? false) morningSlot.PushMinutes.Add(10);
                if (chkMorning20.IsChecked ?? false) morningSlot.PushMinutes.Add(20);
                if (chkMorning30.IsChecked ?? false) morningSlot.PushMinutes.Add(30);
                if (chkMorning40.IsChecked ?? false) morningSlot.PushMinutes.Add(40);
                if (chkMorning50.IsChecked ?? false) morningSlot.PushMinutes.Add(50);

                NotificationSettings.PushTimeSlots.Add(morningSlot);
            }

            // 保存下午时段
            if (chkAfternoonEnabled.IsChecked ?? false)
            {
                var afternoonSlot = new PushTimeSlot
                {
                    StartHour = 6,  // 白天工作时段：6-17点
                    EndHour = 17,
                    IsEnabled = true,
                    PushMinutes = new List<int>()
                };

                if (chkAfternoon00.IsChecked ?? false) afternoonSlot.PushMinutes.Add(0);
                if (chkAfternoon10.IsChecked ?? false) afternoonSlot.PushMinutes.Add(10);
                if (chkAfternoon20.IsChecked ?? false) afternoonSlot.PushMinutes.Add(20);
                if (chkAfternoon30.IsChecked ?? false) afternoonSlot.PushMinutes.Add(30);
                if (chkAfternoon40.IsChecked ?? false) afternoonSlot.PushMinutes.Add(40);
                if (chkAfternoon50.IsChecked ?? false) afternoonSlot.PushMinutes.Add(50);

                NotificationSettings.PushTimeSlots.Add(afternoonSlot);
            }

            // 保存晚间时段
            if (chkEveningEnabled.IsChecked ?? false)
            {
                var eveningSlot = new PushTimeSlot
                {
                    StartHour = 18,  // 晚间时段：18-23点
                    EndHour = 23,
                    IsEnabled = true,
                    PushMinutes = new List<int>()
                };

                if (chkEvening00.IsChecked ?? false) eveningSlot.PushMinutes.Add(0);
                if (chkEvening10.IsChecked ?? false) eveningSlot.PushMinutes.Add(10);
                if (chkEvening20.IsChecked ?? false) eveningSlot.PushMinutes.Add(20);
                if (chkEvening30.IsChecked ?? false) eveningSlot.PushMinutes.Add(30);
                if (chkEvening40.IsChecked ?? false) eveningSlot.PushMinutes.Add(40);
                if (chkEvening50.IsChecked ?? false) eveningSlot.PushMinutes.Add(50);

                NotificationSettings.PushTimeSlots.Add(eveningSlot);
            }

            // 创建或获取应用设置
            AppSettings appSettings;
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                appSettings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                appSettings = new AppSettings();
            }
            
            // 更新推送设置
            appSettings.NotificationSettings = NotificationSettings;
            
            // 保存设置到文件
            var updatedJson = JsonConvert.SerializeObject(appSettings, Formatting.Indented);
            File.WriteAllText(_settingsFilePath, updatedJson);
            
            _logger.LogInformation("推送设置已保存到文件");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存推送设置时发生异常");
            MessageBox.Show($"保存推送设置时出错: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
} 