using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using MySql.Data.MySqlClient;
using Microsoft.Extensions.Logging;
using TCServer.Common.Models;
using TCServer.Common.Interfaces;

namespace TCServer.Data.Repositories
{
    public class AccountRepository : IAccountRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<AccountRepository>? _logger;

        public AccountRepository(ILogger<AccountRepository>? logger = null)
        {
            _connectionString = DatabaseHelper.GetOptimizedConnectionString();
            _logger = logger;
        }

        // 账户信息管理
        public async Task<List<AccountInfo>> GetAllAccountsAsync()
        {
            const string sql = @"
                SELECT 
                    acct_id AS AcctId,
                    acct_name AS AcctName,
                    acct_date AS AcctDate,
                    memo AS Memo,
                    apikey AS ApiKey,
                    secretkey AS SecretKey,
                    apipass AS ApiPass,
                    state AS State,
                    status AS Status,
                    email AS Email,
                    group_id AS GroupId,
                    sendflag AS SendFlag
                FROM acct_info
                ORDER BY acct_id";

            return await DatabaseHelper.ExecuteDbOperationAsync("GetAllAccounts", async connection =>
            {
                var result = await connection.QueryAsync<AccountInfo>(sql);
                return result.ToList();
            }, _logger);
        }

        public async Task<AccountInfo?> GetAccountByIdAsync(int acctId)
        {
            const string sql = @"
                SELECT 
                    acct_id AS AcctId,
                    acct_name AS AcctName,
                    acct_date AS AcctDate,
                    memo AS Memo,
                    apikey AS ApiKey,
                    secretkey AS SecretKey,
                    apipass AS ApiPass,
                    state AS State,
                    status AS Status,
                    email AS Email,
                    group_id AS GroupId,
                    sendflag AS SendFlag
                FROM acct_info
                WHERE acct_id = @AcctId";

            return await DatabaseHelper.ExecuteDbOperationAsync("GetAccountById", async connection =>
            {
                return await connection.QueryFirstOrDefaultAsync<AccountInfo>(sql, new { AcctId = acctId });
            }, _logger);
        }

        public async Task<bool> AddAccountAsync(AccountInfo account)
        {
            const string sql = @"
                INSERT INTO acct_info 
                (acct_name, acct_date, memo, apikey, secretkey, apipass, state, status, email, group_id, sendflag)
                VALUES 
                (@AcctName, @AcctDate, @Memo, @ApiKey, @SecretKey, @ApiPass, @State, @Status, @Email, @GroupId, @SendFlag)";

            return await DatabaseHelper.ExecuteDbOperationAsync("AddAccount", async connection =>
            {
                account.AcctDate = DateTime.Now;
                account.State = 1;
                account.Status = 1;
                account.GroupId = 4; // 默认设置为币安交易所

                var result = await connection.ExecuteAsync(sql, account);
                
                // 如果插入成功，创建初始余额记录
                if (result > 0)
                {
                    // 获取新插入的账户ID
                    var newId = await connection.QuerySingleAsync<int>("SELECT LAST_INSERT_ID()");
                    
                    // 创建初始余额记录
                    await CreateInitialBalanceRecord(connection, newId);
                }
                
                return result > 0;
            }, _logger);
        }

        public async Task<bool> UpdateAccountAsync(AccountInfo account)
        {
            const string sql = @"
                UPDATE acct_info 
                SET acct_name = @AcctName,
                    memo = @Memo,
                    apikey = @ApiKey,
                    secretkey = @SecretKey,
                    apipass = @ApiPass,
                    state = @State,
                    status = @Status,
                    email = @Email,
                    group_id = @GroupId,
                    sendflag = @SendFlag
                WHERE acct_id = @AcctId";

            return await DatabaseHelper.ExecuteDbOperationAsync("UpdateAccount", async connection =>
            {
                var result = await connection.ExecuteAsync(sql, account);
                return result > 0;
            }, _logger);
        }

        public async Task<bool> DeleteAccountAsync(int acctId)
        {
            return await DatabaseHelper.ExecuteDbOperationAsync("DeleteAccount", async connection =>
            {
                using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    // 删除相关的余额记录
                    await connection.ExecuteAsync("DELETE FROM account_balances WHERE account_id = @AcctId", 
                        new { AcctId = acctId }, transaction);
                    
                    // 删除相关的持仓记录
                    await connection.ExecuteAsync("DELETE FROM account_positions WHERE account_id = @AcctId", 
                        new { AcctId = acctId }, transaction);
                    
                    // 删除相关的权益历史记录
                    await connection.ExecuteAsync("DELETE FROM account_equity_history WHERE account_id = @AcctId", 
                        new { AcctId = acctId.ToString() }, transaction);
                    
                    // 删除账户信息
                    var result = await connection.ExecuteAsync("DELETE FROM acct_info WHERE acct_id = @AcctId", 
                        new { AcctId = acctId }, transaction);
                    
                    await transaction.CommitAsync();
                    return result > 0;
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }, _logger);
        }

        // 账户余额管理
        public async Task<AccountBalance?> GetLatestAccountBalanceAsync(int accountId)
        {
            const string sql = @"
                SELECT 
                    id AS Id,
                    account_id AS AccountId,
                    total_equity AS TotalEquity,
                    available_balance AS AvailableBalance,
                    margin_balance AS MarginBalance,
                    unrealized_pnl AS UnrealizedPnl,
                    timestamp AS Timestamp,
                    created_at AS CreatedAt,
                    updated_at AS UpdatedAt
                FROM account_balances
                WHERE account_id = @AccountId
                ORDER BY timestamp DESC
                LIMIT 1";

            return await DatabaseHelper.ExecuteDbOperationAsync("GetLatestAccountBalance", async connection =>
            {
                return await connection.QueryFirstOrDefaultAsync<AccountBalance>(sql, new { AccountId = accountId });
            }, _logger);
        }

        public async Task<List<AccountBalance>> GetAccountBalancesAsync(int accountId)
        {
            const string sql = @"
                SELECT 
                    id AS Id,
                    account_id AS AccountId,
                    total_equity AS TotalEquity,
                    available_balance AS AvailableBalance,
                    margin_balance AS MarginBalance,
                    unrealized_pnl AS UnrealizedPnl,
                    timestamp AS Timestamp,
                    created_at AS CreatedAt,
                    updated_at AS UpdatedAt
                FROM account_balances
                WHERE account_id = @AccountId
                ORDER BY timestamp DESC";

            return await DatabaseHelper.ExecuteDbOperationAsync("GetAccountBalances", async connection =>
            {
                var result = await connection.QueryAsync<AccountBalance>(sql, new { AccountId = accountId });
                return result.ToList();
            }, _logger);
        }

        public async Task<bool> SaveAccountBalanceAsync(AccountBalance balance)
        {
            const string sql = @"
                INSERT INTO account_balances 
                (account_id, total_equity, available_balance, margin_balance, unrealized_pnl, timestamp, created_at, updated_at)
                VALUES 
                (@AccountId, @TotalEquity, @AvailableBalance, @MarginBalance, @UnrealizedPnl, @Timestamp, NOW(), NOW())
                ON DUPLICATE KEY UPDATE
                    total_equity = VALUES(total_equity),
                    available_balance = VALUES(available_balance),
                    margin_balance = VALUES(margin_balance),
                    unrealized_pnl = VALUES(unrealized_pnl),
                    updated_at = NOW()";

            return await DatabaseHelper.ExecuteDbOperationAsync("SaveAccountBalance", async connection =>
            {
                balance.Timestamp = DateTime.Now;
                var result = await connection.ExecuteAsync(sql, balance);
                return result > 0;
            }, _logger);
        }

        // 账户持仓管理
        public async Task<List<AccountPosition>> GetAccountPositionsAsync(int accountId)
        {
            const string sql = @"
                SELECT 
                    id AS Id,
                    account_id AS AccountId,
                    symbol AS Symbol,
                    position_side AS PositionSide,
                    entry_price AS EntryPrice,
                    mark_price AS MarkPrice,
                    position_amt AS PositionAmt,
                    leverage AS Leverage,
                    margin_type AS MarginType,
                    isolated_margin AS IsolatedMargin,
                    unrealized_pnl AS UnrealizedPnl,
                    record_date AS RecordDate,
                    liquidation_price AS LiquidationPrice,
                    timestamp AS Timestamp,
                    created_at AS CreatedAt,
                    updated_at AS UpdatedAt
                FROM account_positions
                WHERE account_id = @AccountId
                ORDER BY timestamp DESC";

            return await DatabaseHelper.ExecuteDbOperationAsync("GetAccountPositions", async connection =>
            {
                var result = await connection.QueryAsync<AccountPosition>(sql, new { AccountId = accountId });
                return result.ToList();
            }, _logger);
        }

        public async Task<List<AccountPosition>> GetAccountPositionsByDateAsync(int accountId, DateTime recordDate)
        {
            const string sql = @"
                SELECT 
                    id AS Id,
                    account_id AS AccountId,
                    symbol AS Symbol,
                    position_side AS PositionSide,
                    entry_price AS EntryPrice,
                    mark_price AS MarkPrice,
                    position_amt AS PositionAmt,
                    leverage AS Leverage,
                    margin_type AS MarginType,
                    isolated_margin AS IsolatedMargin,
                    unrealized_pnl AS UnrealizedPnl,
                    record_date AS RecordDate,
                    liquidation_price AS LiquidationPrice,
                    timestamp AS Timestamp,
                    created_at AS CreatedAt,
                    updated_at AS UpdatedAt
                FROM account_positions
                WHERE account_id = @AccountId AND record_date = @RecordDate
                ORDER BY timestamp DESC";

            return await DatabaseHelper.ExecuteDbOperationAsync("GetAccountPositionsByDate", async connection =>
            {
                var result = await connection.QueryAsync<AccountPosition>(sql, new { AccountId = accountId, RecordDate = recordDate.Date });
                return result.ToList();
            }, _logger);
        }

        public async Task<bool> SaveAccountPositionsAsync(List<AccountPosition> positions)
        {
            if (positions == null || positions.Count == 0)
                return true;

            const string sql = @"
                INSERT INTO account_positions 
                (account_id, symbol, position_side, entry_price, mark_price, position_amt, leverage, 
                 margin_type, isolated_margin, unrealized_pnl, record_date, liquidation_price, timestamp, created_at, updated_at)
                VALUES 
                (@AccountId, @Symbol, @PositionSide, @EntryPrice, @MarkPrice, @PositionAmt, @Leverage,
                 @MarginType, @IsolatedMargin, @UnrealizedPnl, @RecordDate, @LiquidationPrice, @Timestamp, NOW(), NOW())
                ON DUPLICATE KEY UPDATE
                    position_side = VALUES(position_side),
                    entry_price = VALUES(entry_price),
                    mark_price = VALUES(mark_price),
                    position_amt = VALUES(position_amt),
                    leverage = VALUES(leverage),
                    margin_type = VALUES(margin_type),
                    isolated_margin = VALUES(isolated_margin),
                    unrealized_pnl = VALUES(unrealized_pnl),
                    liquidation_price = VALUES(liquidation_price),
                    timestamp = VALUES(timestamp),
                    updated_at = NOW()";

            return await DatabaseHelper.ExecuteDbOperationAsync("SaveAccountPositions", async connection =>
            {
                using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    foreach (var position in positions)
                    {
                        position.Timestamp = DateTime.Now;
                        position.RecordDate = DateTime.Now.Date;
                        await connection.ExecuteAsync(sql, position, transaction);
                    }
                    
                    await transaction.CommitAsync();
                    return true;
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }, _logger);
        }

        // 账户权益历史
        public async Task<List<AccountEquityHistory>> GetAccountEquityHistoryAsync(string accountId, DateTime startDate, DateTime endDate)
        {
            const string sql = @"
                SELECT 
                    id AS Id,
                    account_id AS AccountId,
                    equity AS Equity,
                    available AS Available,
                    position_value AS PositionValue,
                    leverage AS Leverage,
                    long_value AS LongValue,
                    short_value AS ShortValue,
                    long_count AS LongCount,
                    short_count AS ShortCount,
                    create_time AS CreateTime
                FROM account_equity_history
                WHERE account_id = @AccountId 
                    AND create_time >= @StartDate 
                    AND create_time <= @EndDate
                ORDER BY create_time DESC";

            return await DatabaseHelper.ExecuteDbOperationAsync("GetAccountEquityHistory", async connection =>
            {
                var result = await connection.QueryAsync<AccountEquityHistory>(sql, new { 
                    AccountId = accountId, 
                    StartDate = startDate, 
                    EndDate = endDate 
                });
                return result.ToList();
            }, _logger);
        }

        public async Task<bool> SaveAccountEquityHistoryAsync(AccountEquityHistory equity)
        {
            const string sql = @"
                INSERT INTO account_equity_history 
                (account_id, equity, available, position_value, leverage, long_value, short_value, 
                 long_count, short_count, create_time)
                VALUES 
                (@AccountId, @Equity, @Available, @PositionValue, @Leverage, @LongValue, @ShortValue,
                 @LongCount, @ShortCount, @CreateTime)";

            return await DatabaseHelper.ExecuteDbOperationAsync("SaveAccountEquityHistory", async connection =>
            {
                equity.CreateTime = DateTime.Now;
                var result = await connection.ExecuteAsync(sql, equity);
                return result > 0;
            }, _logger);
        }

        // 私有方法：创建初始余额记录
        private async Task CreateInitialBalanceRecord(MySqlConnection connection, int accountId)
        {
            const string sql = @"
                INSERT INTO account_balances 
                (account_id, total_equity, available_balance, margin_balance, unrealized_pnl, timestamp, created_at, updated_at)
                VALUES 
                (@AccountId, 0, 0, 0, 0, NOW(), NOW(), NOW())";

            await connection.ExecuteAsync(sql, new { AccountId = accountId });
        }

        // 实时账户余额管理方法

        /// <summary>
        /// 更新账户实时余额（只保留一条记录）
        /// </summary>
        public async Task UpdateAccountRealTimeBalanceAsync(int accountId, AccountBalance newBalance)
        {
            await DatabaseHelper.ExecuteDbOperationAsync("UpdateAccountRealTimeBalance", async connection =>
            {
                using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    // 1. 获取当前持仓数据来计算Long/Short统计
                    const string positionsSql = @"
                        SELECT position_side AS PositionSide, position_amt AS PositionAmt, mark_price AS MarkPrice
                        FROM account_positions 
                        WHERE account_id = @AccountId AND ABS(position_amt) > 0";
                    
                    var positions = await connection.QueryAsync(positionsSql, new { AccountId = accountId }, transaction);
                    
                    // 计算多空统计
                    decimal longValue = 0m, shortValue = 0m;
                    int longCount = 0, shortCount = 0;
                    
                    foreach (var pos in positions)
                    {
                        var positionValue = Math.Abs(Convert.ToDecimal(pos.PositionAmt)) * Convert.ToDecimal(pos.MarkPrice);
                        var positionSide = pos.PositionSide?.ToString() ?? "";
                        
                        if (positionSide == "LONG")
                        {
                            longValue += positionValue;
                            longCount++;
                        }
                        else if (positionSide == "SHORT")
                        {
                            shortValue += positionValue;
                            shortCount++;
                        }
                    }
                    
                    // 获取平均杠杆
                    const string leverageSql = @"
                        SELECT AVG(leverage) as AvgLeverage FROM account_positions 
                        WHERE account_id = @AccountId AND ABS(position_amt) > 0";
                    
                    var avgLeverage = await connection.QueryFirstOrDefaultAsync<decimal?>(leverageSql, new { AccountId = accountId }, transaction);
                    
                    // 2. 保存新的余额数据到历史表（每次都保存，使用新数据）
                    const string insertHistorySql = @"
                        INSERT INTO account_equity_history 
                        (account_id, equity, available, position_value, leverage, long_value, short_value, long_count, short_count, create_time)
                        VALUES (@AccountId, @Equity, @Available, @PositionValue, @Leverage, @LongValue, @ShortValue, @LongCount, @ShortCount, @CreateTime)";
                    
                    await connection.ExecuteAsync(insertHistorySql, new
                    {
                        AccountId = accountId.ToString(),
                        Equity = newBalance.TotalEquity,  // 使用新数据！
                        Available = newBalance.AvailableBalance,  // 使用新数据！
                        PositionValue = newBalance.MarginBalance - newBalance.AvailableBalance,  // 使用新数据！
                        Leverage = avgLeverage ?? 1m,
                        LongValue = longValue,
                        ShortValue = shortValue,
                        LongCount = longCount,
                        ShortCount = shortCount,
                        CreateTime = newBalance.Timestamp  // 使用新数据的时间！
                    }, transaction);

                    // 3. 删除当前记录
                    const string deleteSql = "DELETE FROM account_balances WHERE account_id = @AccountId";
                    await connection.ExecuteAsync(deleteSql, new { AccountId = accountId }, transaction);

                    // 4. 插入新记录
                    const string insertSql = @"
                        INSERT INTO account_balances 
                        (account_id, total_equity, available_balance, margin_balance, unrealized_pnl, timestamp, created_at, updated_at)
                        VALUES (@AccountId, @TotalEquity, @AvailableBalance, @MarginBalance, @UnrealizedPnl, @Timestamp, NOW(), NOW())";
                    
                    await connection.ExecuteAsync(insertSql, new
                    {
                        AccountId = accountId,
                        newBalance.TotalEquity,
                        newBalance.AvailableBalance,
                        newBalance.MarginBalance,
                        newBalance.UnrealizedPnl,
                        newBalance.Timestamp
                    }, transaction);

                    await transaction.CommitAsync();
                    return true;
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }, _logger);
        }

        /// <summary>
        /// 获取账户实时余额
        /// </summary>
        public async Task<AccountBalance?> GetAccountRealTimeBalanceAsync(int accountId)
        {
            const string sql = @"
                SELECT 
                    account_id AS AccountId,
                    total_equity AS TotalEquity,
                    available_balance AS AvailableBalance,
                    margin_balance AS MarginBalance,
                    unrealized_pnl AS UnrealizedPnl,
                    timestamp AS Timestamp,
                    created_at AS CreatedAt,
                    updated_at AS UpdatedAt
                FROM account_balances 
                WHERE account_id = @AccountId";
            
            return await DatabaseHelper.ExecuteDbOperationAsync("GetAccountRealTimeBalance", async connection =>
            {
                return await connection.QueryFirstOrDefaultAsync<AccountBalance>(sql, new { AccountId = accountId });
            }, _logger);
        }

        /// <summary>
        /// 获取所有账户实时余额
        /// </summary>
        public async Task<List<AccountBalance>> GetAllAccountRealTimeBalancesAsync()
        {
            const string sql = @"
                SELECT 
                    ab.account_id AS AccountId,
                    ab.total_equity AS TotalEquity,
                    ab.available_balance AS AvailableBalance,
                    ab.margin_balance AS MarginBalance,
                    ab.unrealized_pnl AS UnrealizedPnl,
                    ab.timestamp AS Timestamp,
                    ab.created_at AS CreatedAt,
                    ab.updated_at AS UpdatedAt,
                    ai.acct_name AS AccountName,
                    ai.memo AS Memo
                FROM account_balances ab
                LEFT JOIN acct_info ai ON ab.account_id = ai.acct_id
                ORDER BY ai.acct_name";
            
            return await DatabaseHelper.ExecuteDbOperationAsync("GetAllAccountRealTimeBalances", async connection =>
            {
                var results = await connection.QueryAsync(sql);
                var balances = new List<AccountBalance>();
                
                foreach (var row in results)
                {
                    var balance = new AccountBalance
                    {
                        AccountId = row.AccountId,
                        TotalEquity = row.TotalEquity,
                        AvailableBalance = row.AvailableBalance,
                        MarginBalance = row.MarginBalance,
                        UnrealizedPnl = row.UnrealizedPnl,
                        Timestamp = row.Timestamp,
                        CreatedAt = row.CreatedAt,
                        UpdatedAt = row.UpdatedAt
                    };
                    
                    // 使用 memo 作为显示名称，如果为空则使用 acct_name
                    if (row.Memo != null && !string.IsNullOrWhiteSpace(row.Memo.ToString()))
                    {
                        balance.AccountName = row.Memo.ToString();
                    }
                    else if (row.AccountName != null)
                    {
                        balance.AccountName = row.AccountName.ToString();
                    }
                    else
                    {
                        balance.AccountName = $"账户{row.AccountId}";
                    }
                    
                    balances.Add(balance);
                }
                
                return balances;
            }, _logger);
        }

        /// <summary>
        /// 更新账户持仓信息（先删除再插入）
        /// </summary>
        public async Task UpdateAccountPositionsAsync(int accountId, List<AccountPosition> positions)
        {
            await DatabaseHelper.ExecuteDbOperationAsync("UpdateAccountPositions", async connection =>
            {
                using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    // 1. 删除账户所有持仓记录
                    const string deleteSql = "DELETE FROM account_positions WHERE account_id = @AccountId";
                    await connection.ExecuteAsync(deleteSql, new { AccountId = accountId }, transaction);

                    // 2. 插入新的持仓记录（只插入有持仓的）
                    if (positions != null && positions.Count > 0)
                    {
                        var validPositions = positions.Where(p => p.PositionAmt != 0).ToList();
                        
                        if (validPositions.Count > 0)
                        {
                            const string insertSql = @"
                                INSERT INTO account_positions 
                                (account_id, symbol, position_side, entry_price, mark_price, position_amt, 
                                 leverage, margin_type, isolated_margin, unrealized_pnl, record_date, 
                                 liquidation_price, timestamp, created_at, updated_at)
                                VALUES (@AccountId, @Symbol, @PositionSide, @EntryPrice, @MarkPrice, @PositionAmt, 
                                        @Leverage, @MarginType, @IsolatedMargin, @UnrealizedPnl, @RecordDate, 
                                        @LiquidationPrice, @Timestamp, NOW(), NOW())";
                            
                            var now = DateTime.Now;
                            var today = DateTime.Today;
                            
                            foreach (var position in validPositions)
                            {
                                await connection.ExecuteAsync(insertSql, new
                                {
                                    AccountId = accountId,
                                    position.Symbol,
                                    position.PositionSide,
                                    position.EntryPrice,
                                    position.MarkPrice,
                                    position.PositionAmt,
                                    position.Leverage,
                                    position.MarginType,
                                    position.IsolatedMargin,
                                    position.UnrealizedPnl,
                                    RecordDate = today,
                                    position.LiquidationPrice,
                                    Timestamp = now
                                }, transaction);
                            }
                        }
                    }

                    await transaction.CommitAsync();
                    return true;
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }, _logger);
        }
    }
} 