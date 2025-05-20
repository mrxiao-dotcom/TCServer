using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TCServer.Common.Models;

namespace TCServer.Common.Interfaces
{
    public interface IKlineRepository
    {
        Task<bool> SaveKlineDataAsync(KlineData klineData);
        Task<bool> SaveKlineDataListAsync(IEnumerable<KlineData> klineDataList);
        Task<KlineData> GetLatestKlineDataAsync(string symbol);
        Task<IEnumerable<KlineData>> GetKlineDataListAsync(string symbol, DateTime startTime, DateTime endTime);
        Task<bool> DeleteKlineDataAsync(string symbol, DateTime startTime, DateTime endTime);
        Task<DateTime?> GetLatestKlineDateAsync(string symbol);
        Task<bool> SaveKlineDataBatchAsync(IEnumerable<KlineData> klines);
        Task<IEnumerable<string>> GetAllSymbolsAsync();
        Task<bool> HasKlineDataAsync(string symbol, DateTime openTime);
        Task<IEnumerable<string>> GetSymbolsWithDataAsync();
    }
} 