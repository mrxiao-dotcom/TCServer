using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TCServer.Common.Models;

namespace TCServer.Common.Interfaces
{
    public interface IDailyRankingRepository
    {
        /// <summary>
        /// 获取指定日期的排名数据
        /// </summary>
        Task<DailyRanking> GetRankingByDateAsync(DateTime date);
        
        /// <summary>
        /// 获取最近N天的排名数据
        /// </summary>
        Task<IEnumerable<DailyRanking>> GetRecentRankingsAsync(int days);
        
        /// <summary>
        /// 保存排名数据
        /// </summary>
        Task<bool> SaveRankingAsync(DailyRanking ranking);
        
        /// <summary>
        /// 检查指定日期是否已有排名数据
        /// </summary>
        Task<bool> HasRankingForDateAsync(DateTime date);
        
        /// <summary>
        /// 获取需要计算排名的日期列表（比如缺失的日期）
        /// </summary>
        Task<List<DateTime>> GetDatesNeedingRankingCalculationAsync(DateTime startDate, DateTime endDate);

        /// <summary>
        /// 删除指定日期的排名数据
        /// </summary>
        Task<bool> DeleteRankingForDateAsync(DateTime date);
    }
} 