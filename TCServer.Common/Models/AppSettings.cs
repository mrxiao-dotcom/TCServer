using System;
using System.Collections.Generic;

namespace TCServer.Common.Models
{
    public class AppSettings
    {
        public BreakthroughSettings BreakthroughSettings { get; set; } = new BreakthroughSettings();
        public NotificationSettings NotificationSettings { get; set; } = new NotificationSettings();
    }
} 