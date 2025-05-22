using Newtonsoft.Json;
using Serilog;
using TCServer.BreakthroughAlert.Models;
using TCServer.BreakthroughAlert.Services.Interfaces;

namespace TCServer.BreakthroughAlert.Services;

public class FileStorageService : IFileStorageService
{
    private readonly string _configPath;
    private readonly string _logPath;
    private readonly object _lockObj = new();
    private readonly ILogger _logger;

    public FileStorageService(ILogger logger)
    {
        _logger = logger;
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "config");
        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "logs");
        EnsureDirectoriesExist();
    }

    public async Task<T> LoadConfigAsync<T>(string configName) where T : class, new()
    {
        var filePath = Path.Combine(_configPath, $"{configName}.json");
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.Information($"配置文件 {configName} 不存在，创建默认配置");
                return new T();
            }

            var json = await File.ReadAllTextAsync(filePath);
            var config = JsonConvert.DeserializeObject<T>(json);
            return config ?? new T();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"加载配置文件 {configName} 失败");
            return new T();
        }
    }

    public async Task SaveConfigAsync<T>(string configName, T config) where T : class
    {
        var filePath = Path.Combine(_configPath, $"{configName}.json");
        try
        {
            // 创建备份
            if (File.Exists(filePath))
            {
                await BackupConfigAsync(configName);
            }

            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, json);
            _logger.Information($"保存配置文件 {configName} 成功");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"保存配置文件 {configName} 失败");
            throw;
        }
    }

    public async Task AppendLogAsync(string logContent)
    {
        var logFile = Path.Combine(_logPath, $"alert_{DateTime.Now:yyyyMMdd}.json");
        try
        {
            await Task.Run(() =>
            {
                lock (_lockObj)
                {
                    var logs = new List<string>();
                    if (File.Exists(logFile))
                    {
                        var content = File.ReadAllText(logFile);
                        logs = JsonConvert.DeserializeObject<List<string>>(content) ?? new List<string>();
                    }
                    logs.Add(logContent);
                    File.WriteAllText(logFile, JsonConvert.SerializeObject(logs, Formatting.Indented));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "写入日志失败");
        }
    }

    public async Task<List<AlertLog>> LoadRecentLogsAsync(int count)
    {
        var logFile = Path.Combine(_logPath, $"alert_{DateTime.Now:yyyyMMdd}.json");
        try
        {
            if (!File.Exists(logFile))
            {
                return new List<AlertLog>();
            }

            var content = await File.ReadAllTextAsync(logFile);
            var logs = JsonConvert.DeserializeObject<List<string>>(content) ?? new List<string>();
            return logs
                .Select(x => JsonConvert.DeserializeObject<AlertLog>(x))
                .Where(x => x != null)
                .OrderByDescending(x => x.AlertTime)
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "读取日志失败");
            return new List<AlertLog>();
        }
    }

    public async Task CleanOldLogsAsync(int daysToKeep)
    {
        try
        {
            var cutoffDate = DateTime.Now.AddDays(-daysToKeep);
            var logFiles = Directory.GetFiles(_logPath, "alert_*.json");
            
            foreach (var file in logFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith("alert_") && 
                    DateTime.TryParseExact(fileName[6..], "yyyyMMdd", null, 
                        System.Globalization.DateTimeStyles.None, out var fileDate) &&
                    fileDate < cutoffDate)
                {
                    await Task.Run(() => File.Delete(file));
                    _logger.Information($"删除旧日志文件: {file}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "清理旧日志失败");
        }
    }

    public async Task BackupConfigAsync(string configName)
    {
        var filePath = Path.Combine(_configPath, $"{configName}.json");
        var backupPath = Path.Combine(_configPath, $"{configName}.json.bak");
        try
        {
            if (File.Exists(filePath))
            {
                await Task.Run(() => File.Copy(filePath, backupPath, true));
                _logger.Information($"备份配置文件 {configName} 成功");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"备份配置文件 {configName} 失败");
        }
    }

    public async Task RestoreConfigAsync(string configName)
    {
        var filePath = Path.Combine(_configPath, $"{configName}.json");
        var backupPath = Path.Combine(_configPath, $"{configName}.json.bak");
        try
        {
            if (File.Exists(backupPath))
            {
                await Task.Run(() => File.Copy(backupPath, filePath, true));
                _logger.Information($"恢复配置文件 {configName} 成功");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"恢复配置文件 {configName} 失败");
        }
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_configPath);
        Directory.CreateDirectory(_logPath);
    }
} 