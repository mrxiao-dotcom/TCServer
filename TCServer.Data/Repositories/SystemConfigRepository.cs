using System;
using System.Threading.Tasks;
using Dapper;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using TCServer.Common.Models;
using TCServer.Common.Interfaces;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace TCServer.Data.Repositories;

public class SystemConfigRepository : ISystemConfigRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SystemConfigRepository>? _logger;
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 1000;

    public SystemConfigRepository(ILogger<SystemConfigRepository>? logger = null)
    {
        _connectionString = DatabaseHelper.GetOptimizedConnectionString();
        _logger = logger;
    }

    public async Task<T> ExecuteWithRetryAsync<T>(Func<MySqlConnection, Task<T>> operation, string operationName)
    {
        return await DatabaseHelper.ExecuteDbOperationAsync(operationName, operation, _logger);
    }

    public async Task<SystemConfig?> GetConfigAsync(string key)
    {
        const string sql = @"
            SELECT 
                id AS Id, 
                config_key AS `Key`, 
                config_value AS Value, 
                description AS Description, 
                created_at AS CreatedAt, 
                updated_at AS UpdatedAt 
            FROM system_config 
            WHERE config_key = @Key";
            
        return await ExecuteWithRetryAsync(async connection =>
        {
            return await connection.QueryFirstOrDefaultAsync<SystemConfig>(sql, new { Key = key });
        }, "GetConfig");
    }

    public async Task<bool> SaveConfigAsync(SystemConfig config)
    {
        const string sql = @"
            INSERT INTO system_config (config_key, config_value, description, created_at, updated_at)
            VALUES (@Key, @Value, @Description, @CreatedAt, @UpdatedAt)
            ON DUPLICATE KEY UPDATE
            config_value = VALUES(config_value),
            description = VALUES(description),
            updated_at = VALUES(updated_at)";

        return await ExecuteWithRetryAsync(async connection =>
        {
            var affected = await connection.ExecuteAsync(sql, config);
            return affected > 0;
        }, "SaveConfig");
    }

    public async Task<bool> UpdateConfigAsync(string key, string value)
    {
        const string sql = @"
            UPDATE system_config 
            SET config_value = @Value, updated_at = @UpdatedAt
            WHERE config_key = @Key";

        return await ExecuteWithRetryAsync(async connection =>
        {
            var affected = await connection.ExecuteAsync(sql, new 
            { 
                Key = key, 
                Value = value, 
                UpdatedAt = DateTime.Now 
            });
            return affected > 0;
        }, "UpdateConfig");
    }

    public async Task<bool> UpdateConfigAsync(SystemConfig config)
    {
        const string sql = @"
            UPDATE system_config 
            SET config_value = @Value, 
                description = @Description,
                updated_at = @UpdatedAt
            WHERE config_key = @Key";

        return await ExecuteWithRetryAsync(async connection =>
        {
            var affected = await connection.ExecuteAsync(sql, new 
            { 
                Key = config.Key, 
                Value = config.Value, 
                Description = config.Description,
                UpdatedAt = DateTime.Now 
            });
            return affected > 0;
        }, "UpdateConfig");
    }

    public async Task<bool> DeleteConfigAsync(string key)
    {
        const string sql = "DELETE FROM system_config WHERE config_key = @Key";
        
        return await ExecuteWithRetryAsync(async connection =>
        {
            var affected = await connection.ExecuteAsync(sql, new { Key = key });
            return affected > 0;
        }, "DeleteConfig");
    }
} 