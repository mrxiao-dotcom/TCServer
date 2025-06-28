using System.Collections.Generic;

namespace TCServer.Common.Models
{
    /// <summary>
    /// 推送时间段
    /// </summary>
    public class PushTimeSlot
    {
        /// <summary>
        /// 开始时间（小时，0-23）
        /// </summary>
        public int StartHour { get; set; }

        /// <summary>
        /// 结束时间（小时，0-23）
        /// </summary>
        public int EndHour { get; set; }

        /// <summary>
        /// 推送分钟数列表（每个小时的第几分钟推送，0、10、20、30、40、50）
        /// </summary>
        public List<int> PushMinutes { get; set; } = new List<int>();

        /// <summary>
        /// 是否启用此时间段
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 时间段描述
        /// </summary>
        public string Description => $"{StartHour:D2}:00-{EndHour:D2}:59";
    }
} 