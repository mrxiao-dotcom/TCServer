using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TCServer.Common.Models;
using System.Threading;
using System.Net;
using System.Security.Cryptography;
using System.Linq;
using System.Text;

namespace TCServer.Core.Services
{
    public class BinanceApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<BinanceApiService> _logger;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly string[] _baseUrls = new[]
        {
            "https://fapi.binance.com",
            "https://binance-futures.com"
        };
        private int _currentBaseUrlIndex = 0;
        private readonly SemaphoreSlim _requestSemaphore = new SemaphoreSlim(1, 1);
        private DateTime _lastRequestTime = DateTime.MinValue;
        private const int MIN_REQUEST_INTERVAL_MS = 1000;
        private const int MAX_RETRY_COUNT = 5;
        private const int RETRY_DELAY_MS = 2000;
        private const int MAX_FAILURES_BEFORE_SWITCH = 3;
        private int _consecutiveFailures = 0;
        private bool _isRunning = false;
        private static long _operationCounter = 0;

        /// <summary>
        /// 获取服务是否正在运行
        /// </summary>
        public bool IsRunning => _isRunning;

        public BinanceApiService(ILogger<BinanceApiService> logger, string apiKey = "", string apiSecret = "")
        {
            _apiKey = apiKey;
            _apiSecret = apiSecret;
            
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };

            _httpClient = new HttpClient(handler);
            
            // 添加必要的请求头
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", _apiKey);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            
            _logger = logger;
        }

        /// <summary>
        /// 启动服务
        /// </summary>
        public async Task StartAsync()
        {
            if (_isRunning)
            {
                _logger.LogInformation("BinanceApiService 已处于运行状态，无需重复启动");
                return;
            }

            _isRunning = true;
            await Task.CompletedTask;
            _logger.LogInformation("BinanceApiService 已启动");
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning)
            {
                _logger.LogInformation("BinanceApiService 已经停止");
                return;
            }

            _isRunning = false;
            await Task.CompletedTask;
            _logger.LogInformation("BinanceApiService 已停止");
        }

        private string GenerateSignature(string queryString)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        /// <summary>
        /// 获取所有永续合约交易对
        /// </summary>
        public async Task<List<string>> GetPerpetualSymbolsAsync()
        {
            return await ExecuteRequestAsync(async () =>
            {
                var url = $"{GetCurrentBaseUrl()}/fapi/v1/exchangeInfo";
                _logger.LogInformation($"正在请求交易对信息: {url}");
                
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                
                var content = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"获取到交易对信息响应: {content.Length} 字符");
                
                var exchangeInfo = JsonSerializer.Deserialize<ExchangeInfoResponse>(content);
                
                var symbols = new List<string>();
                if (exchangeInfo?.Symbols != null)
                {
                    foreach (var symbol in exchangeInfo.Symbols)
                    {
                        if (symbol.Status == "TRADING" && symbol.ContractType == "PERPETUAL")
                        {
                            symbols.Add(symbol.Symbol);
                        }
                    }
                }
                
                _logger.LogInformation($"解析到 {symbols.Count} 个永续合约交易对");
                return symbols;
            }, "获取交易对信息");
        }

        /// <summary>
        /// 获取K线数据
        /// </summary>
        public async Task<List<KlineDto>> GetKlinesAsync(string symbol, string interval, DateTime startTime, DateTime endTime)
        {
            return await ExecuteRequestAsync(async () =>
            {
                var startTimestamp = new DateTimeOffset(startTime).ToUnixTimeMilliseconds();
                var endTimestamp = new DateTimeOffset(endTime).ToUnixTimeMilliseconds();
                
                var url = $"{GetCurrentBaseUrl()}/fapi/v1/klines?symbol={symbol}&interval={interval}&startTime={startTimestamp}&endTime={endTimestamp}&limit=1000";
                _logger.LogInformation($"正在请求K线数据: {url}");
                
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                
                var content = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"获取到K线数据响应: {content.Length} 字符");
                
                var rawData = JsonSerializer.Deserialize<List<JsonElement[]>>(content);
                var result = new List<KlineDto>();
                
                if (rawData != null)
                {
                    foreach (var item in rawData)
                    {
                        if (item.Length >= 11)
                        {
                            try
                            {
                                var kline = new KlineDto
                                {
                                    OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(item[0].GetInt64()).DateTime,
                                    Open = ParseDecimal(item[1]),
                                    High = ParseDecimal(item[2]),
                                    Low = ParseDecimal(item[3]),
                                    Close = ParseDecimal(item[4]),
                                    Volume = ParseDecimal(item[5]),
                                    CloseTime = DateTimeOffset.FromUnixTimeMilliseconds(item[6].GetInt64()).DateTime,
                                    QuoteVolume = ParseDecimal(item[7]),
                                    TradeCount = item[8].GetInt32(),
                                    TakerBuyVolume = ParseDecimal(item[9]),
                                    TakerBuyQuoteVolume = ParseDecimal(item[10])
                                };
                                
                                result.Add(kline);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"解析{symbol}的K线数据时出错，跳过此条记录");
                            }
                        }
                    }
                }
                
                _logger.LogInformation($"解析到 {result.Count} 条K线数据");
                return result;
            }, $"获取{symbol}的K线数据");
        }

        // 解析十进制数，处理可能的格式问题
        private decimal ParseDecimal(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                string? strValue = element.GetString();
                if (!string.IsNullOrEmpty(strValue))
                {
                    if (decimal.TryParse(strValue, out decimal result))
                    {
                        return result;
                    }
                }
                return 0m;
            }
            else if (element.ValueKind == JsonValueKind.Number)
            {
                return element.GetDecimal();
            }
            
            return 0m;
        }
        
        // 币安API响应模型
        private class ExchangeInfoResponse
        {
            [JsonPropertyName("symbols")]
            public List<SymbolInfo> Symbols { get; set; } = new List<SymbolInfo>();
        }
        
        private class SymbolInfo
        {
            [JsonPropertyName("symbol")]
            public string Symbol { get; set; } = string.Empty;
            
            [JsonPropertyName("status")]
            public string Status { get; set; } = string.Empty;
            
            [JsonPropertyName("contractType")]
            public string ContractType { get; set; } = string.Empty;
        }

        private string GetCurrentBaseUrl()
        {
            return _baseUrls[_currentBaseUrlIndex];
        }

        private void SwitchBaseUrl()
        {
            _currentBaseUrlIndex = (_currentBaseUrlIndex + 1) % _baseUrls.Length;
            _logger.LogInformation($"切换到备用域名: {GetCurrentBaseUrl()}");
        }

        // 添加通用的请求方法，包含重试和频率限制逻辑
        private async Task<T> ExecuteRequestAsync<T>(Func<Task<T>> requestFunc, string operationName)
        {
            var operationId = Interlocked.Increment(ref _operationCounter);
            var startTime = DateTime.Now;
            _logger.LogInformation($"操作ID: {operationId}, 类型: {operationName}, 开始时间: {startTime:HH:mm:ss.fff}");

            try
            {
                await _requestSemaphore.WaitAsync();
                try
                {
                    // 检查请求间隔
                    var timeSinceLastRequest = DateTime.Now - _lastRequestTime;
                    if (timeSinceLastRequest.TotalMilliseconds < MIN_REQUEST_INTERVAL_MS)
                    {
                        var delay = MIN_REQUEST_INTERVAL_MS - (int)timeSinceLastRequest.TotalMilliseconds;
                        _logger.LogInformation($"操作ID: {operationId}, 等待请求间隔: {delay}ms");
                        await Task.Delay(delay);
                    }

                    int retryCount = 0;
                    while (retryCount < MAX_RETRY_COUNT)
                    {
                        try
                        {
                            var result = await requestFunc();
                            _lastRequestTime = DateTime.Now;
                            _consecutiveFailures = 0;
                            
                            var endTime = DateTime.Now;
                            var duration = (endTime - startTime).TotalSeconds;
                            _logger.LogInformation($"操作ID: {operationId}, 类型: {operationName}, 完成时间: {endTime:HH:mm:ss.fff}, 已运行: {duration:F2}秒, 结果: 成功");
                            
                            return result;
                        }
                        catch (HttpRequestException ex)
                        {
                            retryCount++;
                            _consecutiveFailures++;
                            
                            var endTime = DateTime.Now;
                            var duration = (endTime - startTime).TotalSeconds;
                            _logger.LogWarning($"操作ID: {operationId}, 类型: {operationName}, 尝试 {retryCount}/{MAX_RETRY_COUNT} 失败, 已运行: {duration:F2}秒, 错误: {ex.Message}");
                            
                            if (_consecutiveFailures >= MAX_FAILURES_BEFORE_SWITCH)
                            {
                                SwitchBaseUrl();
                                _consecutiveFailures = 0;
                            }
                            
                            if (retryCount >= MAX_RETRY_COUNT)
                            {
                                throw;
                            }
                            
                            await Task.Delay(RETRY_DELAY_MS * retryCount);
                        }
                    }
            
                    throw new Exception($"操作 {operationName} 在 {MAX_RETRY_COUNT} 次重试后仍然失败");
                }
                finally
                {
                    _requestSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                var endTime = DateTime.Now;
                var duration = (endTime - startTime).TotalSeconds;
                _logger.LogError($"操作ID: {operationId}, 类型: {operationName}, 失败时间: {endTime:HH:mm:ss.fff}, 已运行: {duration:F2}秒, 错误: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            if (_isRunning)
            {
                StopAsync().Wait();
            }
            
            _httpClient.Dispose();
            _requestSemaphore.Dispose();
        }
    }
} 