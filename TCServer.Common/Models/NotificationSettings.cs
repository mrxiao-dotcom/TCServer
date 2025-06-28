using System;
using System.Collections.Generic;

namespace TCServer.Common.Models
{
    /// <summary>
    /// 推送配置设置
    /// </summary>
    public class NotificationSettings
    {
        /// <summary>
        /// 是否启用推送
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// 虾推啥Token
        /// </summary>
        public string XtuisToken { get; set; } = string.Empty;

        /// <summary>
        /// 推送时间段设置
        /// </summary>
        public List<PushTimeSlot> PushTimeSlots { get; set; } = new List<PushTimeSlot>();
    }
} 