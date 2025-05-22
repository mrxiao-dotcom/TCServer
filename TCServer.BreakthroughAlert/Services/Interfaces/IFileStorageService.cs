using TCServer.BreakthroughAlert.Models;

namespace TCServer.BreakthroughAlert.Services.Interfaces;

public interface IFileStorageService
{
    /// <summary>
    /// 加载配置
    /// </summary>
    Task<T> LoadConfigAsync<T>(string configName) where T : class, new();

    /// <summary>
    /// 保存配置
    /// </summary>
    Task SaveConfigAsync<T>(string configName, T config) where T : class;

    /// <summary>
    /// 追加日志
    /// </summary>
    Task AppendLogAsync(string logContent);

    /// <summary>
    /// 加载最近日志
    /// </summary>
    Task<List<AlertLog>> LoadRecentLogsAsync(int count);

    /// <summary>
    /// 清理旧日志
    /// </summary>
    Task CleanOldLogsAsync(int daysToKeep);

    /// <summary>
    /// 备份配置
    /// </summary>
    Task BackupConfigAsync(string configName);

    /// <summary>
    /// 恢复配置
    /// </summary>
    Task RestoreConfigAsync(string configName);
} 