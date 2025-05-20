using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySql.Data.MySqlClient;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Security.Authentication;
using System.Collections.Concurrent;

namespace TCServer.Data.Repositories
{
    /// <summary>
    /// 数据库连接帮助类，集中管理连接池和并发控制
    /// </summary>
    public static class DatabaseHelper
    {
        // 信号量控制并行连接数
        private static readonly SemaphoreSlim _dbOperationSemaphore = new SemaphoreSlim(10, 10);
        
        // 活跃连接统计 - 使用volatile确保线程安全
        private static volatile int _activeConnections = 0;
        private static volatile int _maxConcurrentConnections = 0;
        private static readonly object _lockObj = new object();
        private static DateTime _lastResetTime = DateTime.Now;
        
        // 添加连接跟踪
        private static readonly ConcurrentDictionary<int, (DateTime Created, string Operation)> _activeConnectionTracking = new();
        private static int _nextConnectionId = 0;
        
        // 重置连接计数（每小时重置一次）
        private static void ResetConnectionCountIfNeeded()
        {
            var now = DateTime.Now;
            if ((now - _lastResetTime).TotalHours >= 1)
            {
                lock (_lockObj)
                {
                    if ((now - _lastResetTime).TotalHours >= 1)
                    {
                        _activeConnections = 0;
                        _maxConcurrentConnections = 0;
                        _activeConnectionTracking.Clear();
                        _lastResetTime = now;
                    }
                }
            }
        }
        
        // 增加连接计数 - 公开方法供其他类使用
        public static int IncrementConnectionCount(string operation, ILogger? logger = null)
        {
            ResetConnectionCountIfNeeded();
            int connectionId;
            
            lock (_lockObj)
            {
                connectionId = Interlocked.Increment(ref _nextConnectionId);
                _activeConnections++;
                if (_activeConnections > _maxConcurrentConnections)
                {
                    _maxConcurrentConnections = _activeConnections;
                }
                
                _activeConnectionTracking[connectionId] = (DateTime.Now, operation);
                
                // 每5次连接记录一次统计信息
                if (_activeConnections % 5 == 0)
                {
                    var activeConnections = GetActiveConnectionsInfo();
                    var semaphoreAvailable = _dbOperationSemaphore.CurrentCount;
                    var semaphoreTotal = 10; // 信号量总数
                    
                    logger?.LogWarning(
                        "数据库操作状态:\n" +
                        "1. 应用程序连接统计:\n" +
                        $"   - 当前并行操作数: {_activeConnections} (最多允许 {semaphoreTotal} 个)\n" +
                        $"   - 可用操作槽数: {semaphoreAvailable}\n" +
                        $"   - 历史最大并行数: {_maxConcurrentConnections}\n" +
                        "2. 当前活跃操作列表:\n" +
                        string.Join("\n", activeConnections.Select(x => "   " + x)));
                }
            }
            
            return connectionId;
        }
        
        // 减少连接计数 - 公开方法供其他类使用
        public static void DecrementConnectionCount(int connectionId)
        {
            lock (_lockObj)
            {
                if (_activeConnections > 0)
                {
                    _activeConnections--;
                }
                _activeConnectionTracking.TryRemove(connectionId, out _);
            }
        }
        
        // 获取活跃连接信息
        private static List<string> GetActiveConnectionsInfo()
        {
            var now = DateTime.Now;
            var connections = _activeConnectionTracking
                .Select(kvp => new
                {
                    Id = kvp.Key,
                    Operation = kvp.Value.Operation,
                    Created = kvp.Value.Created,
                    Duration = (now - kvp.Value.Created).TotalSeconds
                })
                .OrderByDescending(x => x.Duration)
                .ToList();

            return connections
                .Select(c => $"操作ID: {c.Id}, 类型: {c.Operation}, 开始时间: {c.Created:HH:mm:ss.fff}, 已运行: {c.Duration:F2}秒")
                .ToList();
        }
        
        // 获取连接池统计信息
        public static (int activeConnections, int maxConcurrentConnections, List<string> activeConnectionDetails, int availableSemaphoreCount) GetConnectionStats()
        {
            ResetConnectionCountIfNeeded();
            
            lock (_lockObj)
            {
                return (
                    _activeConnections, 
                    _maxConcurrentConnections, 
                    GetActiveConnectionsInfo(),
                    _dbOperationSemaphore.CurrentCount
                );
            }
        }
        
        // 获取优化的连接字符串
        public static string GetOptimizedConnectionString()
        {
            var builder = new MySqlConnectionStringBuilder(DatabaseConfig.ConnectionString)
            {
                Pooling = true,
                MaximumPoolSize = 50,          // 增加最大连接数
                MinimumPoolSize = 5,           // 设置最小连接数，避免频繁创建连接
                ConnectionTimeout = 15,         // 减少连接超时时间
                ConnectionLifeTime = 300,       // 增加连接生命周期到5分钟
                ConnectionReset = true,
                AllowUserVariables = true,
                ConvertZeroDateTime = true,
                DefaultCommandTimeout = 30,
                SslMode = MySqlSslMode.Disabled,
                AllowPublicKeyRetrieval = true
            };
            return builder.ConnectionString;
        }
        
        // 执行数据库操作
        public static async Task<T> ExecuteDbOperationAsync<T>(string operation, Func<MySqlConnection, Task<T>> operationFunc, ILogger? logger = null)
        {
            var connectionId = 0;
            try
            {
                await _dbOperationSemaphore.WaitAsync();
                connectionId = IncrementConnectionCount(operation, logger);
                
                var connectionString = GetOptimizedConnectionString();
                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();
                
                return await operationFunc(connection);
            }
            finally
            {
                if (connectionId > 0)
                {
                    DecrementConnectionCount(connectionId);
                }
                _dbOperationSemaphore.Release();
            }
        }

        // 添加连接池监控方法
        public static async Task MonitorConnectionPoolAsync(ILogger logger)
        {
            while (true)
            {
                try
                {
                    var stats = GetConnectionStats();
                    if (stats.activeConnections > stats.maxConcurrentConnections * 0.8)
                    {
                        logger.LogWarning(
                            "数据库连接池使用率过高:\n" +
                            $"活动连接数: {stats.activeConnections}\n" +
                            $"最大并发数: {stats.maxConcurrentConnections}\n" +
                            $"可用信号量: {stats.availableSemaphoreCount}\n" +
                            "当前活跃连接:\n" +
                            string.Join("\n", stats.activeConnectionDetails)
                        );
                    }
                    
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "监控数据库连接池时出错");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }
    }
} 