using CloudFlare.Client;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Timer = System.Timers.Timer;

namespace ArashiDNS.Kyro
{
    class Program
    {
        public static Config? FullConfig;
        public static Timer? CheckTimer;

        public static string? GlobalpingToken = string.Empty;

        public static List<MeasurementLocationOption> LocationOptions = new()
        {
            new MeasurementLocationOption
            {
                Limit = 1,
                Country = "CN",
                Asn = 4837 //CU
            },
            new MeasurementLocationOption
            {
                Limit = 1,
                Country = "CN",
                Asn = 4134 //CT
            },
            new MeasurementLocationOption
            {
                Limit = 1,
                Country = "CN",
                Asn = 9808 //CM
            },
            //new MeasurementLocationOption
            //{
            //    Limit = 1,
            //    Country = "CN",
            //    Asn = 37963 //Aliyun
            //},
            //new MeasurementLocationOption
            //{
            //    Limit = 1,
            //    Country = "CN",
            //    Asn = 45090 //Tencent
            //},
        };

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            FullConfig = LoadConfig();
            if (FullConfig == null)
            {
                Console.WriteLine("⛔ Load Config Failed");
                await File.WriteAllTextAsync("config.example.json", JsonSerializer.Serialize(new Config
                {
                    ApiToken = "YOUR-API-TOKEN-HERE",
                    Domains =
                    [
                        new DomainConfig
                        {
                            SubDomain = "sub.example.com",
                            ZoneId = "ZONE-ID-HERE"
                        }
                    ]
                }, new JsonSerializerOptions {WriteIndented = true}));
                return;
            }

            if (File.Exists("globalping.json")) LocationOptions = LoadGlobalPingConfig();
            else if (File.Exists("globalping.example.json"))
                await File.WriteAllTextAsync("globalping.example.json",
                    JsonSerializer.Serialize(LocationOptions, new JsonSerializerOptions {WriteIndented = true}));

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GLOBALPING_TOKEN")))
                GlobalpingToken = Environment.GetEnvironmentVariable("GLOBALPING_TOKEN")?.Trim();
            if (File.Exists("globalping.token"))
                GlobalpingToken = (await File.ReadAllTextAsync("globalping.token")).Trim();

            if (string.IsNullOrWhiteSpace(FullConfig.Node) || FullConfig.Node == "Unknown")
                try
                {
                    FullConfig.Node = await GetGeoInfoAsync();
                    Console.WriteLine("Node: " + FullConfig.Node);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

            if (FullConfig.LogLevel < 2) Console.WriteLine(
                $"Interval: {FullConfig.CheckInterval}ms, Timeout: {FullConfig.Timeout}ms, Port: {FullConfig.CheckPort}");
            await CheckAllDomains();

            CheckTimer = new Timer(FullConfig.CheckInterval);
            CheckTimer.Elapsed += async (sender, e) => await CheckAllDomains();
            CheckTimer.Start();

            Console.WriteLine();
            Console.WriteLine("Application started. Press Ctrl+C / q to shut down.");
            if (!Console.IsInputRedirected && Console.KeyAvailable)
            {
                while (true)
                    if (Console.ReadKey().KeyChar == 'q')
                        Environment.Exit(0);
            }

            EventWaitHandle wait = new AutoResetEvent(false);
            while (true) wait.WaitOne();
        }

        static Config LoadConfig()
        {
            try
            {
                var json = File.ReadAllText("config.json");
                return JsonSerializer.Deserialize<Config>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠  Load Config Error: {ex.Message}");
                return null;
            }
        }

        static List<MeasurementLocationOption> LoadGlobalPingConfig()
        {
            try
            {
                var json = File.ReadAllText("globalping.json");
                return JsonSerializer.Deserialize<List<MeasurementLocationOption>>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠  Load Config Error: {ex.Message}");
                return null;
            }
        }

        static async Task CheckAllDomains()
        {
            if (FullConfig.LogLevel < 2) Console.WriteLine($"\n=== Health Check Start {DateTime.Now} ===");

            if (FullConfig.UseParallel)
                await Parallel.ForEachAsync(FullConfig.Domains, CheckDomain);
            else
                foreach (var domainConfig in FullConfig.Domains)
                    await CheckDomain(domainConfig, new CancellationToken(false));

            if (FullConfig.LogLevel < 1) Console.WriteLine($"=== Health Check End {DateTime.Now} ===\n");
        }

        private static async ValueTask CheckDomain(DomainConfig domainConfig, CancellationToken _)
        {
            try
            {
                await ProcessDomain(domainConfig);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {domainConfig.SubDomain}: {ex.Message} {DateTime.Now}");
            }
        }

        static async Task ProcessDomain(DomainConfig domainConfig)
        {
            if (string.IsNullOrWhiteSpace(domainConfig.SubDomain)) return;
            if (FullConfig.LogLevel < 2) Console.WriteLine($"{domainConfig.SubDomain,-28}: Checking");

            var client = new CloudFlareClient(FullConfig.ApiToken);
            var haName = string.IsNullOrWhiteSpace(domainConfig.HADomain)
                ? $"{FullConfig.HaPrefix}.{domainConfig.SubDomain}"
                : domainConfig.HADomain;

            var haRecords = (await client.Zones.DnsRecords.GetAsync(domainConfig.ZoneId,
                new DnsRecordFilter() {Name = haName})).Result.Where(x =>
                x.Type is DnsRecordType.A or DnsRecordType.Cname or DnsRecordType.Txt);
            if (!haRecords.Any())
            {
                //Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"{domainConfig.SubDomain,-28}: ⚠  HA NotFound: {haName}");
                //Console.ResetColor();
                return;
            }

            if ((domainConfig.UseCurrentFirst ?? false) || (FullConfig.UseCurrentFirst ?? false))
            {
                var currentRecord = (await client.Zones.DnsRecords.GetAsync(domainConfig.ZoneId,
                        new DnsRecordFilter() {Name = haName})).Result
                    .First(x => x.Type is DnsRecordType.A or DnsRecordType.Cname);
                if (currentRecord != null && await IsRecordAccessible(currentRecord, domainConfig))
                {
                    if (FullConfig.LogLevel < 1)
                        Console.WriteLine(
                            $"{domainConfig.SubDomain,-28}: ✓ Current Record is Accessible [{currentRecord.Content}]");
                    return;
                }
            }

            var accessibleRecords = new List<DnsRecord>();
            foreach (var record in haRecords)
            {
                if (await IsRecordAccessible(record, domainConfig))
                {
                    accessibleRecords.Add(record);
                    //Console.ForegroundColor = ConsoleColor.DarkGreen;
                    if (FullConfig.LogLevel < 1) Console.WriteLine($"{domainConfig.SubDomain,-28}: ✓ {record.Name,-30} [{record.Content,-40}] UP");
                    //Console.ResetColor();
                }
                else if (FullConfig.LogLevel < 1)
                {
                    //Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"{domainConfig.SubDomain,-28}: ✗ {record.Name,-30} [{record.Content,-40}] DOWN");
                    //Console.ResetColor();
                }
            }

            if (!accessibleRecords.Any())
            {
                //Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"{domainConfig.SubDomain,-28}: ⚠  No Accessible HA: {haName}");
                //Console.ResetColor();
                return;
            }

            var bestRecord = accessibleRecords.OrderByDescending(r => r.Ttl).First();
            if (bestRecord.Type == DnsRecordType.Txt)
                bestRecord = new DnsRecord()
                {
                    Type = bestRecord.Content.Trim('"').Split(':').First().ToUpper() == "A"
                        ? DnsRecordType.A
                        : DnsRecordType.Cname,
                    Content = bestRecord.Content.Trim('"').Split(':').Last(),
                    Ttl = bestRecord.Ttl,
                    Proxied = bestRecord.Proxied
                };

            var dnsRecords = (await client.Zones.DnsRecords.GetAsync(domainConfig.ZoneId,
                new DnsRecordFilter() { Name = domainConfig.SubDomain })).Result;
            var mainRecord = dnsRecords
                .FirstOrDefault(r =>
                    r.Name == domainConfig.SubDomain && r.Type is DnsRecordType.A or DnsRecordType.Cname);

            if (mainRecord != null &&
                mainRecord.Content == bestRecord.Content &&
                mainRecord.Type == bestRecord.Type)
            {
                if (FullConfig.LogLevel < 2) Console.WriteLine($"{domainConfig.SubDomain,-28}: No Update Needed / {bestRecord.Content}");
                return;
            }

            if (mainRecord != null)
            {
                await client.Zones.DnsRecords.DeleteAsync(domainConfig.ZoneId, mainRecord.Id);
                if (FullConfig.LogLevel < 1) Console.WriteLine($"{domainConfig.SubDomain,-28}: Deleted Old Record ");
            }

            var newRecord = new NewDnsRecord()
            {
                Name = domainConfig.SubDomain,
                Type = bestRecord.Type,
                Content = bestRecord.Content,
                Ttl = bestRecord.Ttl,
                Proxied = bestRecord.Proxied,
                Comment = $"LastUpdate@{DateTime.Now:yyyy-MM-ddTHH:mm:sszzz}@{FullConfig.Node}"
            };

            await client.Zones.DnsRecords.AddAsync(domainConfig.ZoneId, newRecord);
            if (FullConfig.LogLevel < 3)
                Console.WriteLine(
                    $"{domainConfig.SubDomain,-28}: Updated / {bestRecord.Content} ({bestRecord.Type}) @ {DateTime.Now}");
        }

        static async Task<bool> IsRecordAccessible(DnsRecord record, DomainConfig domainConfig)
        {
            try
            {
                IPAddress[] addresses;
                switch (record.Type)
                {
                    case DnsRecordType.Cname:
                    {
                        addresses = await GetDnsIpAddresses(record.Content);
                        break;
                    }
                    case DnsRecordType.Txt:
                    {
                        var sp = record.Content.Trim('"').Split(':');
                        addresses = sp.First().ToUpper() == "A"
                            ? [IPAddress.Parse(sp.Last())]
                            : await GetDnsIpAddresses(sp.Last());
                        break;
                    }
                    case DnsRecordType.A:
                        addresses = [IPAddress.Parse(record.Content)];
                        break;
                    default:
                        return false;
                }

                var timeOut = domainConfig.Timeout ?? FullConfig.Timeout;
                var port = domainConfig.CheckPort ?? FullConfig.CheckPort;
                var retries = domainConfig.Retries ?? FullConfig.Retries;
                var isIcmp = domainConfig.UseICMPing ?? FullConfig.UseICMPing;
                var isGlobal = domainConfig.UseGlobalPing ?? FullConfig.UseGlobalPing;

                if (!addresses.Any()) return false;
                if (!FullConfig.CheckAllNode) addresses = [addresses.First()];

                foreach (var address in addresses)
                {
                    var uri = string.IsNullOrWhiteSpace(domainConfig.CheckUrl)
                        ? new Uri($"http://{address}:{port}")
                        : new Uri(domainConfig.CheckUrl);
                    var count = 0;

                    if (isGlobal)
                    {
                        try
                        {
                            var res = isIcmp
                                ? await GlobalICMPing(address, timeOut)
                                : await GlobalTCPing(address, port, timeOut);

                            foreach (var stats in res)
                            {
                                if (stats.Rcv == 0) return false;
                                if (FullConfig.CheckPacketLoss && stats.Loss > 100 - (100 * FullConfig.PacketLossRatio))
                                    return false;
                            }

                            return true;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                            throw;
                        }
                    }

                    for (var i = 0; i < retries; i++)
                    {
                        if (domainConfig.UseCurl ?? false
                                ? await CurlPing(uri, timeOut, address,
                                    domainConfig.CurlAcceptCode ?? 200)
                                : isIcmp
                                    ? await ICMPing(address, timeOut)
                                    : await TCPing(address, port, timeOut))
                        {
                            if (!FullConfig.CheckPacketLoss) return true;
                            count++;
                            if (count >= retries * FullConfig.PacketLossRatio) return true;
                        }

                        await Task.Delay(300);
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        static async Task<bool> TCPing(IPAddress ip, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync(ip, port);
                var timeoutTask = Task.Delay(timeoutMs);

                var completedTask = await Task.WhenAny(task, timeoutTask);
                if (completedTask == timeoutTask) return false;

                await task;
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> ICMPing(IPAddress ip, int timeoutMs)
        {
            var bufferBytes = Encoding.Default.GetBytes("abcdefghijklmnopqrstuvwabcdefghi");
            return (await new Ping().SendPingAsync(ip, timeoutMs, bufferBytes)).Status == IPStatus.Success;
        }

        public static async Task<PingStats[]> GlobalICMPing(IPAddress ip, int timeoutMs)
        {
            return (await new GlobalpingClient(token: GlobalpingToken)
                    .PingWithCountriesAsync(ip.ToString(), LocationOptions))!
                .Results.Select(x => x.Result.Stats).ToArray();
        }

        public static async Task<PingStats[]> GlobalTCPing(IPAddress ip, int port, int timeoutMs)
        {
            return (await new GlobalpingClient(token: GlobalpingToken)
                    .TCPingWithCountriesAsync(ip.ToString(), LocationOptions, port))!
                .Results.Select(x => x.Result.Stats).ToArray();
        }

        public static async Task<bool> CurlPing(Uri uri, int timeoutMs, IPAddress ipAddress, int code = 200)
        {
            try
            {
                var newUri = new UriBuilder(uri) {Host = ipAddress.ToString()}.Uri;
                var response = await new HttpClient
                        {Timeout = TimeSpan.FromMilliseconds(timeoutMs), DefaultRequestHeaders = {Host = uri.Host}}
                    .GetAsync(newUri);
                return response.IsSuccessStatusCode || (int) response.StatusCode == code;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<string> GetGeoInfoAsync()
        {
            using var httpClient = new HttpClient();
            string json;
            try
            {
                json = await httpClient.GetStringAsync("https://api.ip.sb/geoip");
            }
            catch (Exception)
            {
                json = await httpClient.GetStringAsync("https://ip.ns.net.kg/json");
            }
            var doc = JsonDocument.Parse(json).RootElement;
            var str = string.Empty;

            str += doc.TryGetProperty("country_code", out var c) ? c + "," : "";
            str += doc.TryGetProperty("region_code", out var r) ? r + "," : "";
            str += doc.TryGetProperty("city", out var ct) ? ct + "," : "";
            str += doc.TryGetProperty("asn", out var a) ? a : "";

            return str;
        }

        static async Task<IPAddress[]> GetDnsIpAddresses(string domain)
        {
            try
            {
                using var client = new HttpClient();
                var response = await client.GetStringAsync($"{FullConfig.DoH}?name={domain}");

                return JObject.Parse(response)["Answer"]!
                    .Where(a => a["type"]?.Value<int>() == 1)
                    .Select(a => IPAddress.Parse(a["data"]?.Value<string>() ?? "0.0.0.0"))
                    .ToArray();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return (await Dns.GetHostEntryAsync(domain)).AddressList;
            }
        }
    }

    public class Config
    {
        public string ApiToken { get; set; }
        public string HaPrefix { get; set; } = "_ha";
        public string Node { get; set; } = "Unknown";
        public string DoH { get; set; } = "https://dns.pub/dns-query";
        public int CheckInterval { get; set; } = 60 * 1000; // 60s
        public int Timeout { get; set; } = 3000; // 3s
        public int CheckPort { get; set; } = 80;
        public int Retries { get; set; } = 10;
        public int LogLevel { get; set; } = 0;
        public bool UseICMPing { get; set; } = false;
        public bool UseGlobalPing { get; set; } = false;
        public bool UseParallel { get; set; } = false;
        public bool CheckAllNode { get; set; } = false;
        public bool CheckPacketLoss { get; set; } = false;
        public double PacketLossRatio { get; set; } = 0.8;
        public bool? UseCurrentFirst { get; set; }
        public List<DomainConfig> Domains { get; set; }
    }

    public class DomainConfig
    {
        public string? HADomain { get; set; } = string.Empty;
        public string SubDomain { get; set; }
        public string ZoneId { get; set; } = string.Empty;
        public int? Timeout { get; set; }
        public int? CheckPort { get; set; }
        public string? CheckUrl { get; set; }
        public int? Retries { get; set; }
        public bool? UseICMPing { get; set; }
        public bool? UseGlobalPing { get; set; }
        public bool? UseCurl { get; set; }
        public bool? UseCurrentFirst { get; set; }
        public int? CurlAcceptCode { get; set; } = 200;

    }

}