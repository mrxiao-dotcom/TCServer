using System.Threading.Tasks;
using TCServer.Common.Models;

namespace TCServer.Common.Interfaces
{
    public interface ISystemConfigRepository
    {
        Task<SystemConfig?> GetConfigAsync(string key);
        Task<bool> SaveConfigAsync(SystemConfig config);
        Task<bool> UpdateConfigAsync(SystemConfig config);
        Task<bool> DeleteConfigAsync(string key);
    }
} 