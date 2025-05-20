using System;

namespace TCServer.Common.Models
{
    /// <summary>
    /// 每日涨跌幅排名数据
    /// </summary>
    public class DailyRanking
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string TopGainers { get; set; } = string.Empty;
        public string TopLosers { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 排名项
    /// </summary>
    public class RankingItem
    {
        public int Rank { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public decimal Percentage { get; set; }

        public override string ToString()
        {
            return $"{Rank}#{Symbol}#{Percentage:P2}";
        }

        public static RankingItem Parse(string text)
        {
            string[] parts = text.Split('#');
            if (parts.Length != 3)
                throw new FormatException("排名项格式不正确");

            return new RankingItem
            {
                Rank = int.Parse(parts[0]),
                Symbol = parts[1],
                Percentage = decimal.Parse(parts[2].TrimEnd('%')) / 100m
            };
        }
    }
} 