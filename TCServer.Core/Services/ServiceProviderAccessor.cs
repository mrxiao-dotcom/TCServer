using System;

namespace TCServer.Core.Services
{
    /// <summary>
    /// 提供对全局ServiceProvider的访问
    /// </summary>
    public static class ServiceProviderAccessor
    {
        public static IServiceProvider? ServiceProvider { get; set; }
    }
} 