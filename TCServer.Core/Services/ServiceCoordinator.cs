using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TCServer.Core.Services
{
    /// <summary>
    /// 服务协调器，负责安全地启动和停止多个相关服务
    /// </summary>
    public class ServiceCoordinator
    {
        private readonly BinanceApiService _binanceApiService;
        private readonly KlineService _klineService;
        private readonly AccountDataService _accountDataService;
        private readonly ILogger<ServiceCoordinator> _logger;
        
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private volatile bool _isStarting = false;
        private volatile bool _isStopping = false;
        
        public ServiceCoordinator(
            BinanceApiService binanceApiService,
            KlineService klineService,
            AccountDataService accountDataService,
            ILogger<ServiceCoordinator> logger)
        {
            _binanceApiService = binanceApiService;
            _klineService = klineService;
            _accountDataService = accountDataService;
            _logger = logger;
        }
        
        public bool IsRunning { get; private set; }
        public bool IsStarting => _isStarting;
        public bool IsStopping => _isStopping;
        
        /// <summary>
        /// 按顺序安全启动所有服务
        /// </summary>
        public async Task<bool> StartAllServicesAsync()
        {
            await _operationLock.WaitAsync();
            try
            {
                if (IsRunning || _isStarting)
                {
                    _logger.LogWarning("服务已在运行或正在启动中，跳过启动请求");
                    return false;
                }
                
                _isStarting = true;
                _logger.LogInformation("=== 服务协调器开始启动所有服务 ===");
                
                try
                {
                    // 步骤1：启动币安API服务
                    _logger.LogInformation("📡 步骤1: 启动BinanceApiService");
                    await _binanceApiService.StartAsync();
                    _logger.LogInformation("✅ BinanceApiService启动完成");
                    
                    // 等待API服务稳定
                    await Task.Delay(1500);
                    
                    // 步骤2：启动K线服务
                    _logger.LogInformation("📊 步骤2: 启动KlineService");
                    await _klineService.StartAsync();
                    _logger.LogInformation("✅ KlineService启动完成");
                    
                    // 等待K线服务稳定
                    await Task.Delay(2000);
                    
                    // 步骤3：启动账户数据服务
                    _logger.LogInformation("👤 步骤3: 启动AccountDataService");
                    _accountDataService.StartQuery();
                    _logger.LogInformation("✅ AccountDataService启动完成");
                    
                    IsRunning = true;
                    _logger.LogInformation("🎉 所有服务启动完成");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "启动服务时发生异常，正在回滚");
                    
                    // 启动失败时安全停止已启动的服务
                    await SafeStopAllServicesInternal();
                    
                    throw;
                }
            }
            finally
            {
                _isStarting = false;
                _operationLock.Release();
            }
        }
        
        /// <summary>
        /// 按顺序安全停止所有服务
        /// </summary>
        public async Task<bool> StopAllServicesAsync()
        {
            await _operationLock.WaitAsync();
            try
            {
                if (!IsRunning || _isStopping)
                {
                    _logger.LogWarning("服务未运行或正在停止中，跳过停止请求");
                    return false;
                }
                
                _isStopping = true;
                _logger.LogInformation("=== 服务协调器开始停止所有服务 ===");
                
                await SafeStopAllServicesInternal();
                
                IsRunning = false;
                _logger.LogInformation("🛑 所有服务停止完成");
                return true;
            }
            finally
            {
                _isStopping = false;
                _operationLock.Release();
            }
        }
        
        /// <summary>
        /// 内部安全停止方法
        /// </summary>
        private async Task SafeStopAllServicesInternal()
        {
            try
            {
                // 步骤1：停止账户数据服务（最快）
                _logger.LogInformation("👤 步骤1: 停止AccountDataService");
                _accountDataService.StopQuery();
                _logger.LogInformation("✅ AccountDataService已停止");
                
                await Task.Delay(1000);
                
                // 步骤2：停止K线服务
                _logger.LogInformation("📊 步骤2: 停止KlineService");
                await _klineService.StopAsync();
                _logger.LogInformation("✅ KlineService已停止");
                
                await Task.Delay(1000);
                
                // 步骤3：停止币安API服务
                _logger.LogInformation("📡 步骤3: 停止BinanceApiService");
                await _binanceApiService.StopAsync();
                _logger.LogInformation("✅ BinanceApiService已停止");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止服务时发生异常");
            }
        }
        
        /// <summary>
        /// 获取服务状态信息
        /// </summary>
        public ServiceStatus GetStatus()
        {
            return new ServiceStatus
            {
                IsRunning = IsRunning,
                IsStarting = _isStarting,
                IsStopping = _isStopping,
                BinanceApiRunning = _binanceApiService.IsRunning,
                KlineServiceFetching = _klineService.IsFetching,
                AccountDataRunning = _accountDataService.IsRunning
            };
        }
    }
    
    /// <summary>
    /// 服务状态信息
    /// </summary>
    public class ServiceStatus
    {
        public bool IsRunning { get; set; }
        public bool IsStarting { get; set; }
        public bool IsStopping { get; set; }
        public bool BinanceApiRunning { get; set; }
        public bool KlineServiceFetching { get; set; }
        public bool AccountDataRunning { get; set; }
        
        public override string ToString()
        {
            return $"总体状态: {(IsRunning ? "运行中" : "已停止")}, " +
                   $"币安API: {(BinanceApiRunning ? "运行" : "停止")}, " +
                   $"K线服务: {(KlineServiceFetching ? "运行" : "停止")}, " +
                   $"账户服务: {(AccountDataRunning ? "运行" : "停止")}";
        }
    }
} 