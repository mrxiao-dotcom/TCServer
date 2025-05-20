using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySql.Data.MySqlClient;
using TCServer.Common.Models;
using TCServer.Common.Interfaces;
using Microsoft.Extensions.Logging;
using TCServer.Core.Services;

namespace TCServer.Data.Repositories;

public class KlineRepository : IKlineRepository
{
    private readonly string _connectionString;
    private readonly ILogger<KlineRepository> _logger;
    private readonly BinanceApiService _binanceApiService;
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 1000;
    
    // 添加信号量控制并行连接数
    private static readonly SemaphoreSlim _dbOperationSemaphore = new SemaphoreSlim(10, 10); // 最多允许10个并行数据库操作

    // 添加连接池统计信息
    private static int _activeConnections = 0;
    private static int _maxConcurrentConnections = 0;
    private static readonly object _lockObj = new object();

    private void IncrementConnectionCount()
    {
        lock (_lockObj)
        {
            _activeConnections++;
            if (_activeConnections > _maxConcurrentConnections)
            {
                _maxConcurrentConnections = _activeConnections;
            }
            
            // 每10次连接记录一次统计信息
            if (_activeConnections % 10 == 0)
            {
                _logger.LogWarning("数据库连接池状态 - 活动连接: {ActiveCount}, 历史最大并发: {MaxConcurrent}", 
                    _activeConnections, _maxConcurrentConnections);
            }
        }
    }

    private void DecrementConnectionCount()
    {
        lock (_lockObj)
        {
            _activeConnections--;
        }
    }

    public KlineRepository(ILogger<KlineRepository> logger, BinanceApiService binanceApiService)
    {
        _connectionString = DatabaseHelper.GetOptimizedConnectionString();
        _logger = logger;
        _binanceApiService = binanceApiService;
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<MySqlConnection, Task<T>> operation, string operationName)
    {
        return await DatabaseHelper.ExecuteDbOperationAsync(operationName, operation, _logger);
    }

    private bool IsRetryableError(MySqlException ex)
    {
        // 定义可重试的错误码
        int[] retryableErrorCodes = new[]
        {
            1040, // Too many connections
            1205, // Lock wait timeout
            1213, // Deadlock found
            2006, // MySQL server has gone away
            2013, // Lost connection to MySQL server
            2014, // Commands out of sync
            2026, // SSL connection error
            2055, // Lost connection to MySQL server during query
            0     // 通用错误（包括连接池超时）
        };
        return retryableErrorCodes.Contains(ex.Number);
    }

    public async Task<bool> SaveKlineDataAsync(KlineData kline)
    {
        const string sql = @"
            INSERT INTO kline_data 
            (symbol, open_time, open_price, high_price, low_price, close_price, volume, 
             close_time, quote_volume, trades, taker_buy_volume, taker_buy_quote_volume, created_at)
            VALUES 
            (@Symbol, @OpenTime, @OpenPrice, @HighPrice, @LowPrice, @ClosePrice, @Volume,
             @CloseTime, @QuoteVolume, @TradeCount, @TakerBuyVolume, @TakerBuyQuoteVolume, @CreatedAt)
            ON DUPLICATE KEY UPDATE
            open_price = VALUES(open_price),
            high_price = VALUES(high_price),
            low_price = VALUES(low_price),
            close_price = VALUES(close_price),
            volume = VALUES(volume),
            quote_volume = VALUES(quote_volume),
            trades = VALUES(trades),
            taker_buy_volume = VALUES(taker_buy_volume),
            taker_buy_quote_volume = VALUES(taker_buy_quote_volume)";

        return await ExecuteWithRetryAsync(async connection =>
        {
            var affected = await connection.ExecuteAsync(sql, kline);
            return affected > 0;
        }, "SaveKlineData");
    }

    public async Task<bool> SaveKlineDataBatchAsync(IEnumerable<KlineData> klines)
    {
        const string sql = @"
            INSERT INTO kline_data 
            (symbol, open_time, open_price, high_price, low_price, close_price, volume, 
             close_time, quote_volume, trades, taker_buy_volume, taker_buy_quote_volume, created_at)
            VALUES 
            (@Symbol, @OpenTime, @OpenPrice, @HighPrice, @LowPrice, @ClosePrice, @Volume,
             @CloseTime, @QuoteVolume, @TradeCount, @TakerBuyVolume, @TakerBuyQuoteVolume, @CreatedAt)
            ON DUPLICATE KEY UPDATE
            open_price = VALUES(open_price),
            high_price = VALUES(high_price),
            low_price = VALUES(low_price),
            close_price = VALUES(close_price),
            volume = VALUES(volume),
            quote_volume = VALUES(quote_volume),
            trades = VALUES(trades),
            taker_buy_volume = VALUES(taker_buy_volume),
            taker_buy_quote_volume = VALUES(taker_buy_quote_volume)";

        return await ExecuteWithRetryAsync(async connection =>
        {
            var affected = await connection.ExecuteAsync(sql, klines);
            return affected > 0;
        }, "SaveKlineDataBatch");
    }

    public async Task<DateTime?> GetLatestKlineDateAsync(string symbol)
    {
        const string sql = "SELECT MAX(open_time) FROM kline_data WHERE symbol = @Symbol";
        
        return await ExecuteWithRetryAsync(async connection =>
        {
            return await connection.QueryFirstOrDefaultAsync<DateTime?>(sql, new { Symbol = symbol });
        }, "GetLatestKlineDate");
    }

    public async Task<IEnumerable<string>> GetAllSymbolsAsync()
    {
        const string sql = "SELECT DISTINCT symbol FROM kline_data";
        return await ExecuteWithRetryAsync(async connection =>
        {
            return await connection.QueryAsync<string>(sql);
        }, "GetAllSymbols");
    }

    public async Task<bool> HasKlineDataAsync(string symbol, DateTime openTime)
    {
        const string sql = "SELECT COUNT(1) FROM kline_data WHERE symbol = @Symbol AND open_time = @OpenTime";
        return await ExecuteWithRetryAsync(async connection =>
        {
            var count = await connection.ExecuteScalarAsync<int>(sql, new { Symbol = symbol, OpenTime = openTime });
            return count > 0;
        }, "HasKlineData");
    }
    
    // 新增接口实现
    public async Task<bool> SaveKlineDataListAsync(IEnumerable<KlineData> klineDataList)
    {
        // 直接调用已实现的批量保存方法
        return await SaveKlineDataBatchAsync(klineDataList);
    }

    public async Task<KlineData> GetLatestKlineDataAsync(string symbol)
    {
        const string sql = @"SELECT 
                          id AS Id,
                          symbol AS Symbol,
                          open_time AS OpenTime,
                          open_price AS OpenPrice,
                          high_price AS HighPrice,
                          low_price AS LowPrice,
                          close_price AS ClosePrice,
                          volume AS Volume,
                          close_time AS CloseTime,
                          quote_volume AS QuoteVolume,
                          trades AS TradeCount,
                          taker_buy_volume AS TakerBuyVolume,
                          taker_buy_quote_volume AS TakerBuyQuoteVolume,
                          created_at AS CreatedAt,
                          updated_at AS UpdatedAt
                          FROM kline_data 
                          WHERE symbol = @Symbol 
                          ORDER BY open_time DESC 
                          LIMIT 1";
                          
        return await ExecuteWithRetryAsync(async connection =>
        {
            return await connection.QueryFirstOrDefaultAsync<KlineData>(sql, new { Symbol = symbol });
        }, "GetLatestKlineData");
    }

    public async Task<IEnumerable<KlineData>> GetKlineDataListAsync(string symbol, DateTime startTime, DateTime endTime)
    {
        const string sql = @"SELECT 
                          id AS Id,
                          symbol AS Symbol,
                          open_time AS OpenTime,
                          open_price AS OpenPrice,
                          high_price AS HighPrice,
                          low_price AS LowPrice,
                          close_price AS ClosePrice,
                          volume AS Volume,
                          close_time AS CloseTime,
                          quote_volume AS QuoteVolume,
                          trades AS TradeCount,
                          taker_buy_volume AS TakerBuyVolume,
                          taker_buy_quote_volume AS TakerBuyQuoteVolume,
                          created_at AS CreatedAt,
                          updated_at AS UpdatedAt
                          FROM kline_data 
                          WHERE symbol = @Symbol 
                          AND open_time BETWEEN @StartTime AND @EndTime 
                          ORDER BY open_time";
                          
        return await ExecuteWithRetryAsync(async connection =>
        {
            return await connection.QueryAsync<KlineData>(sql, new { Symbol = symbol, StartTime = startTime, EndTime = endTime });
        }, "GetKlineDataList");
    }

    public async Task<bool> DeleteKlineDataAsync(string symbol, DateTime startTime, DateTime endTime)
    {
        const string sql = @"DELETE FROM kline_data 
                          WHERE symbol = @Symbol 
                          AND open_time BETWEEN @StartTime AND @EndTime";
        return await ExecuteWithRetryAsync(async connection =>
        {
            var affected = await connection.ExecuteAsync(sql, new { Symbol = symbol, StartTime = startTime, EndTime = endTime });
            return affected > 0;
        }, "DeleteKlineData");
    }

    public async Task<IEnumerable<string>> GetSymbolsWithDataAsync()
    {
        const string sql = @"
            SELECT DISTINCT symbol 
            FROM kline_data 
            WHERE open_time >= DATE_SUB(CURDATE(), INTERVAL 1 DAY)
            ORDER BY symbol";
            
        return await ExecuteWithRetryAsync(async connection =>
        {
            return await connection.QueryAsync<string>(sql);
        }, "GetSymbolsWithData");
    }
} 