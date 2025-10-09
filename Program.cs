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
        public static Config FullConfig;
        public static Timer CheckTimer;

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            FullConfig = LoadConfig();
            if (FullConfig == null)
            {
                Console.WriteLine("⛔ Load Config Failed");
                await File.WriteAllTextAsync("config.example.json", JsonSerializer.Serialize(new Config()
                {
                    ApiToken = "your-api-token-here",
                    Domains = new List<DomainConfig>()
                    {
                        new DomainConfig()
                        {
                            SubDomain = "sub.example.com",
                            ZoneId = "zoneid-here"
                        }
                    }
                }, new JsonSerializerOptions() { WriteIndented = true }));
                return;
            }

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

        static async Task CheckAllDomains()
        {
            if (FullConfig.LogLevel < 2) Console.WriteLine($"\n=== Health Check Start {DateTime.Now} ===");

            foreach (var domainConfig in FullConfig.Domains)
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

            if (FullConfig.LogLevel < 1) Console.WriteLine($"=== Health Check End {DateTime.Now} ===\n");
        }


        static async Task ProcessDomain(DomainConfig domainConfig)
        {
            if (string.IsNullOrWhiteSpace(domainConfig.SubDomain)) return;
            if (FullConfig.LogLevel < 2) Console.WriteLine($"- Check: {domainConfig.SubDomain}");

            var client = new CloudFlareClient(FullConfig.ApiToken);
            var haName = string.IsNullOrWhiteSpace(domainConfig.HADomain)
                ? $"_ha.{domainConfig.SubDomain}"
                : domainConfig.HADomain;

            var haRecords = (await client.Zones.DnsRecords.GetAsync(domainConfig.ZoneId,
                new DnsRecordFilter() {Name = haName})).Result.Where(x =>
                x.Type is DnsRecordType.A or DnsRecordType.Cname or DnsRecordType.Txt);
            if (!haRecords.Any())
            {
                Console.WriteLine($"    ⚠  HA NotFound: {haName} / {domainConfig.SubDomain}");
                return;
            }

            var accessibleRecords = new List<DnsRecord>();
            foreach (var record in haRecords)
            {
                if (await IsRecordAccessible(record, domainConfig))
                {
                    accessibleRecords.Add(record);
                    if (FullConfig.LogLevel < 1) Console.WriteLine($"  - ✓ {record.Name} ({record.Content}) UP");
                }
                else if (FullConfig.LogLevel < 1) Console.WriteLine($"  - ✗ {record.Name} ({record.Content}) DOWN");
            }

            if (!accessibleRecords.Any())
            {
                Console.WriteLine($"    ⚠  No Accessible HA: {haName} / {domainConfig.SubDomain}");
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
                if (FullConfig.LogLevel < 2) Console.WriteLine($"    No Update Needed : {domainConfig.SubDomain} / {bestRecord.Content}");
                return;
            }

            if (mainRecord != null)
            {
                await client.Zones.DnsRecords.DeleteAsync(domainConfig.ZoneId, mainRecord.Id);
                if (FullConfig.LogLevel < 1) Console.WriteLine($"    - Deleted Old Record : {domainConfig.SubDomain}");
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
            if (FullConfig.LogLevel < 3) Console.WriteLine($"    - Updated {domainConfig.SubDomain} : {bestRecord.Content} ({bestRecord.Type})");
        }

        static async Task<bool> IsRecordAccessible(DnsRecord record, DomainConfig domainConfig)
        {
            try
            {
                IPAddress[] addresses;
                var timeOut = domainConfig.Timeout ?? FullConfig.Timeout;
                var port = domainConfig.CheckPort ?? FullConfig.CheckPort;
                var retries = domainConfig.Retries ?? FullConfig.Retries;
                var isIcmp = domainConfig.UseICMPing ?? FullConfig.UseICMPing;

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

                if (!addresses.Any()) return false;
                for (var i = 0; i < retries; i++)
                {
                    if (isIcmp
                            ? await ICMPing(addresses.First(), timeOut)
                            : await TCPing(addresses.First(), port, timeOut))
                        return true;

                    await Task.Delay(300);
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
                json = await httpClient.GetStringAsync("https://myip.mili.one/json");
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
        public string Node { get; set; } = "Unknown";
        public string DoH { get; set; } = "https://dns.pub/dns-query";
        public int CheckInterval { get; set; } = 60 * 1000; // 60s
        public int Timeout { get; set; } = 1000; // 1s
        public int CheckPort { get; set; } = 80;
        public int Retries { get; set; } = 4;
        public int LogLevel { get; set; } = 0;
        public bool UseICMPing { get; set; } = false;
        public List<DomainConfig> Domains { get; set; }
    }

    public class DomainConfig
    {
        public string? HADomain { get; set; } = string.Empty;
        public string SubDomain { get; set; }
        public string ZoneId { get; set; } = string.Empty;
        public int? Timeout { get; set; }
        public int? CheckPort { get; set; }
        public int? Retries { get; set; }
        public bool? UseICMPing { get; set; }

    }

}