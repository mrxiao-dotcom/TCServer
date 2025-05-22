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
using System.Threading;

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
            "https://fapi.binance.com",  // 币安U本位合约API
            "https://fapi1.binance.com", // 币安U本位合约API备用地址1
            "https://fapi2.binance.com"  // 币安U本位合约API备用地址2
        };
        private int _currentBaseUrlIndex = 0;
        private readonly SemaphoreSlim _requestSemaphore = new SemaphoreSlim(3, 3); // 允许多个并发请求
        private readonly SemaphoreSlim _rateLimitSemaphore = new SemaphoreSlim(1, 1); // 用于频率限制
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
                CookieContainer = new CookieContainer(),
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            
            // 更新请求头
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", _apiKey);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            _httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
            _httpClient.DefaultRequestHeaders.Add("Pragma", "no-cache");
            
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
                try
                {
                    //_logger.LogInformation("开始创建CancellationTokenSource");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    //_logger.LogInformation("CancellationTokenSource创建完成");
                    
                    // 使用U本位合约API的exchangeInfo接口
                    var url = $"{GetCurrentBaseUrl()}/fapi/v1/exchangeInfo";
                    //_logger.LogInformation($"准备发送HTTP请求: {url}");
                    
                    //_logger.LogInformation("开始发送HTTP请求...");
                    var response = await _httpClient.GetAsync(url, cts.Token);
                    //_logger.LogInformation($"HTTP请求完成，状态码: {(int)response.StatusCode}");
                    
                    //_logger.LogInformation("开始读取响应内容...");
                    var responseContent = await response.Content.ReadAsStringAsync();
                   // _logger.LogInformation($"响应内容读取完成，长度: {responseContent?.Length ?? 0} 字符");
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError($"获取合约交易对信息失败: HTTP {(int)response.StatusCode}, 响应: {responseContent}");
                        throw new HttpRequestException($"获取合约交易对信息失败: HTTP {(int)response.StatusCode}, 响应: {responseContent}");
                    }
                    
                    if (string.IsNullOrEmpty(responseContent))
                    {
                        _logger.LogWarning("获取到的合约交易对信息为空");
                        return new List<string>();
                    }
                    
                    //_logger.LogInformation($"开始解析JSON响应...");
                    try
                    {
                        var exchangeInfo = JsonSerializer.Deserialize<ExchangeInfoResponse>(responseContent);
                        //_logger.LogInformation("JSON解析完成");
                        
                        if (exchangeInfo?.Symbols == null)
                        {
                            _logger.LogWarning("解析合约交易对信息失败：Symbols为空");
                            return new List<string>();
                        }
                        
                        //_logger.LogInformation($"开始处理 {exchangeInfo.Symbols.Count} 个交易对...");
                        
                        // 记录所有交易对的信息
                        foreach (var symbol in exchangeInfo.Symbols)
                        {
                            //_logger.LogDebug($"交易对信息: Symbol={symbol.Symbol}, Status={symbol.Status}, ContractType={symbol.ContractType}, QuoteAsset={symbol.QuoteAsset}");
                        }
                        
                        var symbols = new List<string>();
                        foreach (var symbol in exchangeInfo.Symbols)
                        {
                            try
                            {
                                // 检查交易对状态和合约类型
                                if (symbol.Status == "TRADING" && 
                                    symbol.ContractType == "PERPETUAL" && 
                                    symbol.QuoteAsset == "USDT")  // 使用QuoteAsset而不是MarginAsset
                                {
                                    symbols.Add(symbol.Symbol);
                                    //_logger.LogInformation($"添加交易对: {symbol.Symbol}, 状态: {symbol.Status}, 合约类型: {symbol.ContractType}, 计价资产: {symbol.QuoteAsset}");
                                }
                                else
                                {
                                    _logger.LogDebug($"跳过交易对: {symbol.Symbol}, 状态: {symbol.Status}, 合约类型: {symbol.ContractType}, 计价资产: {symbol.QuoteAsset}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"处理交易对 {symbol.Symbol} 时出错，跳过此交易对");
                            }
                        }
                        
                        //_logger.LogInformation($"交易对处理完成，找到 {symbols.Count} 个符合条件的交易对");
                        
                        if (!symbols.Any())
                        {
                            _logger.LogWarning("未找到任何活跃的USDT永续合约交易对");
                            // 记录一些示例数据以便调试
                            var sampleSymbols = exchangeInfo.Symbols.Take(5).Select(s => 
                                $"Symbol={s.Symbol}, Status={s.Status}, ContractType={s.ContractType}, QuoteAsset={s.QuoteAsset}");
                            _logger.LogWarning($"示例交易对信息: {string.Join(", ", sampleSymbols)}");
                            return new List<string>();
                        }
                        
                        return symbols;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, $"解析合约交易对信息时发生JSON解析错误，响应内容: {responseContent}");
                        throw;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogError("获取合约交易对信息超时");
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, $"获取合约交易对信息时发生HTTP请求错误: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"获取合约交易对信息时发生未知错误: {ex.Message}, 堆栈: {ex.StackTrace}");
                    throw;
                }
            }, "获取合约交易对信息");
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
        
        /// <summary>
        /// 获取24小时行情数据
        /// </summary>
        public async Task<List<TickerPriceDto>> Get24hrTickerAsync()
        {
            return await ExecuteRequestAsync(async () =>
            {
                try
                {
                    // 首先获取所有活跃的永续合约
                    //_logger.LogInformation("开始获取永续合约列表...");
                    var activeSymbols = await GetPerpetualSymbolsAsync();
                    if (activeSymbols == null || !activeSymbols.Any())
                    {
                        _logger.LogWarning("未获取到活跃的永续合约列表，返回空数据");
                        return new List<TickerPriceDto>();
                    }
                   // _logger.LogInformation($"成功获取到 {activeSymbols.Count} 个活跃永续合约");

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    
                    // 使用U本位合约API的24小时行情接口
                    var url = $"{GetCurrentBaseUrl()}/fapi/v1/ticker/24hr";
                    //_logger.LogInformation($"正在请求24小时行情数据: {url}");
                    
                    var response = await _httpClient.GetAsync(url, cts.Token);
                    var responseContent = await response.Content.ReadAsStringAsync();
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError($"获取24小时行情数据失败: HTTP {(int)response.StatusCode}, 响应: {responseContent}");
                        throw new HttpRequestException($"获取24小时行情数据失败: HTTP {(int)response.StatusCode}");
                    }
                    
                    if (string.IsNullOrEmpty(responseContent))
                    {
                        _logger.LogWarning("获取到的24小时行情数据为空");
                        return new List<TickerPriceDto>();
                    }
                    
                    //_logger.LogInformation($"获取到24小时行情数据响应: {responseContent.Length} 字符");
                    //_logger.LogDebug($"响应内容: {responseContent}");
                    
                    var tickers = JsonSerializer.Deserialize<List<TickerPriceDto>>(responseContent);
                    if (tickers == null)
                    {
                        _logger.LogWarning("解析24小时行情数据失败，返回空数据");
                        return new List<TickerPriceDto>();
                    }
                    
                    // 过滤数据：只保留USDT合约和活跃的合约
                    var filteredTickers = tickers
                        .Where(t => t.Symbol.EndsWith("USDT") && activeSymbols.Contains(t.Symbol))
                        .ToList();
                    
                    //_logger.LogInformation($"成功解析到 {filteredTickers.Count} 个活跃交易对的24小时行情数据");
                    
                    // 验证数据有效性
                    foreach (var ticker in filteredTickers)
                    {
                        if (ticker.LastPrice <= 0 || ticker.OpenPrice <= 0)
                        {
                            _logger.LogWarning($"交易对 {ticker.Symbol} 的价格数据无效: LastPrice={ticker.LastPrice}, OpenPrice={ticker.OpenPrice}");
                        }
                    }
                    
                    return filteredTickers;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogError("获取24小时行情数据超时");
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "获取24小时行情数据时发生HTTP请求错误");
                    throw;
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "解析24小时行情数据时发生JSON解析错误");
                    throw;
                        }
                        catch (Exception ex)
                        {
                    _logger.LogError(ex, "获取24小时行情数据时发生未知错误");
                    throw;
                }
            }, "获取24小时行情数据");
        }

        // 24小时行情数据模型
        public class TickerPriceDto
        {
            [JsonPropertyName("symbol")]
            public string Symbol { get; set; } = string.Empty;

            [JsonPropertyName("priceChange")]
            [JsonConverter(typeof(DecimalStringConverter))]
            public decimal PriceChange { get; set; }

            [JsonPropertyName("priceChangePercent")]
            [JsonConverter(typeof(DecimalStringConverter))]
            public decimal PriceChangePercent { get; set; }

            [JsonPropertyName("lastPrice")]
            [JsonConverter(typeof(DecimalStringConverter))]
            public decimal LastPrice { get; set; }

            [JsonPropertyName("openPrice")]
            [JsonConverter(typeof(DecimalStringConverter))]
            public decimal OpenPrice { get; set; }

            [JsonPropertyName("highPrice")]
            [JsonConverter(typeof(DecimalStringConverter))]
            public decimal HighPrice { get; set; }

            [JsonPropertyName("lowPrice")]
            [JsonConverter(typeof(DecimalStringConverter))]
            public decimal LowPrice { get; set; }

            [JsonPropertyName("volume")]
            [JsonConverter(typeof(DecimalStringConverter))]
            public decimal Volume { get; set; }

            [JsonPropertyName("quoteVolume")]
            [JsonConverter(typeof(DecimalStringConverter))]
            public decimal QuoteVolume { get; set; }
        }

        // 用于处理字符串类型的decimal值的转换器
        private class DecimalStringConverter : JsonConverter<decimal>
        {
            public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    string? strValue = reader.GetString();
                    if (!string.IsNullOrEmpty(strValue) && decimal.TryParse(strValue, out decimal result))
                    {
                        return result;
                    }
                }
                else if (reader.TokenType == JsonTokenType.Number)
                {
                    return reader.GetDecimal();
                }
                return 0m;
            }

            public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
            {
                writer.WriteNumberValue(value);
            }
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

            [JsonPropertyName("quoteAsset")]
            public string QuoteAsset { get; set; } = string.Empty;
        }

        private string GetCurrentBaseUrl()
        {
            return _baseUrls[_currentBaseUrlIndex];
        }

        private void SwitchBaseUrl()
        {
            _currentBaseUrlIndex = (_currentBaseUrlIndex + 1) % _baseUrls.Length;
            _logger.LogInformation($"切换到备用API地址: {GetCurrentBaseUrl()}");
        }

        // 添加通用的请求方法，包含重试和频率限制逻辑
        private async Task<T> ExecuteRequestAsync<T>(Func<Task<T>> requestFunc, string operationName)
        {
            var operationId = Interlocked.Increment(ref _operationCounter);
            var startTime = DateTime.Now;
            //_logger.LogInformation($"操作ID: {operationId}, 类型: {operationName}, 开始时间: {startTime:HH:mm:ss.fff}, 线程ID: {Thread.CurrentThread.ManagedThreadId}");

            try
            {
                //_logger.LogInformation($"操作ID: {operationId}, 等待请求信号量...");
                await _requestSemaphore.WaitAsync();
                //_logger.LogInformation($"操作ID: {operationId}, 获得请求信号量");
                
                int retryCount = 0;
                while (retryCount < MAX_RETRY_COUNT)
                {
                    try
                    {
                        // 使用单独的信号量处理频率限制
                        await _rateLimitSemaphore.WaitAsync();
                        try
                        {
                            var timeSinceLastRequest = (DateTime.Now - _lastRequestTime).TotalMilliseconds;
                            if (timeSinceLastRequest < MIN_REQUEST_INTERVAL_MS)
                            {
                                var delay = (int)(MIN_REQUEST_INTERVAL_MS - timeSinceLastRequest);
                                //_logger.LogInformation($"操作ID: {operationId}, 等待请求间隔: {delay}ms");
                                await Task.Delay(delay);
                            }
                        }
                        finally
                        {
                            _rateLimitSemaphore.Release();
                        }

                        //_logger.LogInformation($"操作ID: {operationId}, 开始执行请求函数, 重试次数: {retryCount}");
                        var result = await requestFunc();
                        //_logger.LogInformation($"操作ID: {operationId}, 请求函数执行完成");
                        
                        _lastRequestTime = DateTime.Now;
                        _consecutiveFailures = 0;
                        
                        var endTime = DateTime.Now;
                        var duration = (endTime - startTime).TotalSeconds;
                        //_logger.LogInformation($"操作ID: {operationId}, 类型: {operationName}, 完成时间: {endTime:HH:mm:ss.fff}, 已运行: {duration:F2}秒, 结果: 成功");
                        
                        return result;
                    }
                    catch (HttpRequestException ex)
                    {
                        retryCount++;
                    _consecutiveFailures++;
                        
                        var endTime = DateTime.Now;
                        var duration = (endTime - startTime).TotalSeconds;
                        //_logger.LogWarning($"操作ID: {operationId}, 类型: {operationName}, 尝试 {retryCount}/{MAX_RETRY_COUNT} 失败, 已运行: {duration:F2}秒, 错误: {ex.Message}, 堆栈: {ex.StackTrace}");
                    
                    if (_consecutiveFailures >= MAX_FAILURES_BEFORE_SWITCH)
                    {
                        SwitchBaseUrl();
                        _consecutiveFailures = 0;
                            _logger.LogInformation($"操作ID: {operationId}, 已切换到备用API地址: {GetCurrentBaseUrl()}");
                        }
                        
                        if (retryCount >= MAX_RETRY_COUNT)
                        {
                            _logger.LogError($"操作ID: {operationId}, 达到最大重试次数，放弃重试");
                            throw;
                        }
                        
                        var delay = RETRY_DELAY_MS * Math.Pow(2, retryCount - 1);
                        var actualDelay = (int)Math.Min(delay, 10000);
                        _logger.LogInformation($"操作ID: {operationId}, 等待 {actualDelay}ms 后重试");
                        await Task.Delay(actualDelay);
                    }
                    catch (TaskCanceledException ex)
                    {
                        retryCount++;
                    _consecutiveFailures++;
                        
                        var endTime = DateTime.Now;
                        var duration = (endTime - startTime).TotalSeconds;
                        //_logger.LogWarning($"操作ID: {operationId}, 类型: {operationName}, 尝试 {retryCount}/{MAX_RETRY_COUNT} 超时, 已运行: {duration:F2}秒, 错误: {ex.Message}, 堆栈: {ex.StackTrace}");
                    
                    if (_consecutiveFailures >= MAX_FAILURES_BEFORE_SWITCH)
                    {
                        SwitchBaseUrl();
                        _consecutiveFailures = 0;
                            _logger.LogInformation($"操作ID: {operationId}, 已切换到备用API地址: {GetCurrentBaseUrl()}");
                        }
                        
                        if (retryCount >= MAX_RETRY_COUNT)
                        {
                            _logger.LogError($"操作ID: {operationId}, 达到最大重试次数，放弃重试");
                            throw new TimeoutException($"操作 {operationName} 在 {MAX_RETRY_COUNT} 次尝试后仍然超时");
                        }
                        
                        var delay = RETRY_DELAY_MS * Math.Pow(2, retryCount - 1);
                        var actualDelay = (int)Math.Min(delay, 10000);
                        _logger.LogInformation($"操作ID: {operationId}, 等待 {actualDelay}ms 后重试");
                        await Task.Delay(actualDelay);
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        _consecutiveFailures++;
                        
                        var endTime = DateTime.Now;
                        var duration = (endTime - startTime).TotalSeconds;
                        _logger.LogError($"操作ID: {operationId}, 类型: {operationName}, 尝试 {retryCount}/{MAX_RETRY_COUNT} 发生异常, 已运行: {duration:F2}秒, 错误: {ex.Message}, 堆栈: {ex.StackTrace}");
                        
                        if (retryCount >= MAX_RETRY_COUNT)
                        {
                            _logger.LogError($"操作ID: {operationId}, 达到最大重试次数，放弃重试");
                            throw;
                        }
                        
                        var delay = RETRY_DELAY_MS * Math.Pow(2, retryCount - 1);
                        var actualDelay = (int)Math.Min(delay, 10000);
                        _logger.LogInformation($"操作ID: {operationId}, 等待 {actualDelay}ms 后重试");
                        await Task.Delay(actualDelay);
                    }
                }
                
                throw new Exception($"操作 {operationName} 在 {MAX_RETRY_COUNT} 次重试后仍然失败");
            }
            catch (Exception ex)
            {
                var endTime = DateTime.Now;
                var duration = (endTime - startTime).TotalSeconds;
                _logger.LogError($"操作ID: {operationId}, 类型: {operationName}, 失败时间: {endTime:HH:mm:ss.fff}, 已运行: {duration:F2}秒, 错误: {ex.Message}, 堆栈: {ex.StackTrace}");
                throw;
            }
            finally
            {
                //_logger.LogInformation($"操作ID: {operationId}, 释放请求信号量");
                _requestSemaphore.Release();
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
            _rateLimitSemaphore.Dispose();
        }
    }
} 