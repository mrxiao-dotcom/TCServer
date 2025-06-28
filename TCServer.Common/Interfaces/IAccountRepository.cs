using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TCServer.Common.Models;

namespace TCServer.Common.Interfaces
{
    /// <summary>
    /// 账户数据访问接口
    /// </summary>
    public interface IAccountRepository
    {
        // 账户信息管理
        Task<List<AccountInfo>> GetAllAccountsAsync();
        Task<AccountInfo?> GetAccountByIdAsync(int acctId);
        Task<bool> AddAccountAsync(AccountInfo account);
        Task<bool> UpdateAccountAsync(AccountInfo account);
        Task<bool> DeleteAccountAsync(int acctId);

        // 账户余额管理
        Task<AccountBalance?> GetLatestAccountBalanceAsync(int accountId);
        Task<List<AccountBalance>> GetAccountBalancesAsync(int accountId);
        Task<bool> SaveAccountBalanceAsync(AccountBalance balance);

        // 账户持仓管理
        Task<List<AccountPosition>> GetAccountPositionsAsync(int accountId);
        Task<List<AccountPosition>> GetAccountPositionsByDateAsync(int accountId, DateTime recordDate);
        Task<bool> SaveAccountPositionsAsync(List<AccountPosition> positions);

        // 账户权益历史
        Task<List<AccountEquityHistory>> GetAccountEquityHistoryAsync(string accountId, DateTime startDate, DateTime endDate);
        Task<bool> SaveAccountEquityHistoryAsync(AccountEquityHistory equity);

        // 实时账户余额管理
        Task UpdateAccountRealTimeBalanceAsync(int accountId, AccountBalance newBalance);
        Task<AccountBalance?> GetAccountRealTimeBalanceAsync(int accountId);
        Task<List<AccountBalance>> GetAllAccountRealTimeBalancesAsync();
        Task UpdateAccountPositionsAsync(int accountId, List<AccountPosition> positions);
    }
} 