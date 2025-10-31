using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArashiDNS.Kyro
{
    public class GlobalpingClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl = "https://api.globalping.io";

        public GlobalpingClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.BaseAddress = new Uri(_baseUrl);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ArashiDNS.Kyro/0.1");
        }

        public void SetBearerToken(string token)
        {
            if (!string.IsNullOrWhiteSpace(token))
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<CreateMeasurementResponse?> CreateMeasurementAsync(MeasurementRequest request,
            CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/v1/measurements", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<CreateMeasurementResponse>(responseJson, JsonOptions);
        }

        public async Task<MeasurementResponse?> GetMeasurementAsync(string id,
            CancellationToken cancellationToken = default)
        {
            var response = await _httpClient.GetAsync($"/v1/measurements/{id}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<MeasurementResponse>(json, JsonOptions);
        }

        /// <summary>
        /// 执行Ping测量并自动等待结果
        /// </summary>
        /// <param name="target">目标地址</param>
        /// <param name="countries">国家代码列表</param>
        /// <param name="limit">每个国家的探针数量限制</param>
        /// <param name="packets">数据包数量</param>
        /// <param name="maxWaitSeconds">最大等待时间（秒）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>测量结果</returns>
        public async Task<MeasurementResponse?> PingWithCountriesAsync(
            string target,
            List<string> countries,
            int limit = 1,
            int packets = 3,
            int maxWaitSeconds = 30,
            CancellationToken cancellationToken = default)
        {
            var locations = new List<MeasurementLocationOption>();
            foreach (var country in countries)
            {
                locations.Add(new MeasurementLocationOption
                {
                    Country = country,
                    Limit = limit
                });
            }

            var request = new MeasurementRequest
            {
                Type = "ping",
                Target = target,
                Locations = locations,
                MeasurementOptions = new MeasurementPingOptions
                {
                    Packets = packets
                },
                InProgressUpdates = true
            };

            var createResponse = await CreateMeasurementAsync(request, cancellationToken);
            if (createResponse != null)
            {
                var measurementId = createResponse?.Id;

                var startTime = DateTime.UtcNow;
                while (DateTime.UtcNow - startTime < TimeSpan.FromSeconds(maxWaitSeconds))
                {
                    var measurement = await GetMeasurementAsync(measurementId, cancellationToken);
                    if (measurement?.Status != "in-progress") return measurement;

                    await Task.Delay(250, cancellationToken);
                }
            }

            throw new TimeoutException($"Measurement did not complete within {maxWaitSeconds} seconds");
        }

        /// <summary>
        /// 执行TCPing测量并自动等待结果
        /// </summary>
        /// <param name="target">目标地址</param>
        /// <param name="countries">国家代码列表</param>
        /// <param name="port">端口</param>
        /// <param name="limit">每个国家的探针数量限制</param>
        /// <param name="packets">数据包数量</param>
        /// <param name="maxWaitSeconds">最大等待时间（秒）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>测量结果</returns>
        public async Task<MeasurementResponse?> TCPingWithCountriesAsync(
            string target,
            List<string> countries,
            int port = 80,
            int limit = 1,
            int packets = 3,
            int maxWaitSeconds = 30,
            CancellationToken cancellationToken = default)
        {
            var locations = new List<MeasurementLocationOption>();
            foreach (var country in countries)
            {
                locations.Add(new MeasurementLocationOption
                {
                    Country = country,
                    Limit = limit
                });
            }

            var request = new MeasurementRequest
            {
                Type = "ping",
                Target = target,
                Locations = locations,
                MeasurementOptions = new MeasurementPingOptions
                {
                    Packets = packets,
                    Protocol = "TCP",
                    Port = port
                },
                InProgressUpdates = true
            };

            var createResponse = await CreateMeasurementAsync(request, cancellationToken);
            if (createResponse != null)
            {
                var measurementId = createResponse?.Id;

                var startTime = DateTime.UtcNow;
                while (DateTime.UtcNow - startTime < TimeSpan.FromSeconds(maxWaitSeconds))
                {
                    var measurement = await GetMeasurementAsync(measurementId, cancellationToken);
                    if (measurement?.Status != "in-progress") return measurement;

                    // 等待250毫秒后重试
                    await Task.Delay(250, cancellationToken);
                }
            }

            throw new TimeoutException($"Measurement did not complete within {maxWaitSeconds} seconds");
        }

        private static JsonSerializerOptions? JsonOptions => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = {new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)}
        };
    }

    public class MeasurementRequest
    {
        public string Type { get; set; }
        public string Target { get; set; }
        public bool InProgressUpdates { get; set; } = false;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Locations { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Limit { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object MeasurementOptions { get; set; }
    }

    public class MeasurementLocationOption
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Country { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Limit { get; set; }
    }

    public class MeasurementPingOptions
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Packets { get; set; } = 3;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Protocol { get; set; } = "ICMP";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Port { get; set; }
    }

    public class CreateMeasurementResponse
    {
        public string Id { get; set; }
        public int ProbesCount { get; set; }
    }

    public class MeasurementResponse
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Target { get; set; }
        public int ProbesCount { get; set; }
        public List<MeasurementResultItem> Results { get; set; }
    }

    public class MeasurementResultItem
    {
        public ResultProbe Probe { get; set; }
        public TestResult Result { get; set; }
    }

    public class ResultProbe
    {
        public string Continent { get; set; }
        public string Region { get; set; }
        public string Country { get; set; }
        public string State { get; set; }
        public string City { get; set; }
        public int Asn { get; set; }
        public string Network { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public class TestResult
    {
        public string Status { get; set; }
        public string RawOutput { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ResolvedAddress { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ResolvedHostname { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PingTiming> Timings { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PingStats Stats { get; set; }
    }

    public class PingTiming
    {
        public double Rtt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Ttl { get; set; }
    }

    public class PingStats
    {
        public double? Min { get; set; }
        public double? Avg { get; set; }
        public double? Max { get; set; }
        public int Total { get; set; }
        public int Rcv { get; set; }
        public int Drop { get; set; }
        public double Loss { get; set; }
    }
}
