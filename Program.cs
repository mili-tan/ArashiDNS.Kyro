using CloudFlare.Client;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using System.Net;
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
                return;
            }

            Console.WriteLine(
                $"Interval: {FullConfig.CheckInterval}ms, Timeout: {FullConfig.Timeout}ms, Port: {FullConfig.CheckPort}");
            await CheckAllDomains();

            CheckTimer = new Timer(FullConfig.CheckInterval);
            CheckTimer.Elapsed += async (sender, e) => await CheckAllDomains();
            CheckTimer.Start();

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();

            CheckTimer.Stop();
            CheckTimer.Dispose();
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
            Console.WriteLine($"\n=== Health Check Start {DateTime.Now} ===");

            foreach (var domainConfig in FullConfig.Domains)
            {
                try
                {
                    await ProcessDomain(domainConfig);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {domainConfig.SubDomain}: {ex.Message}");
                }
            }

            Console.WriteLine($"=== Health Check End {DateTime.Now} ===\n");
        }


        static async Task ProcessDomain(DomainConfig domainConfig)
        {
            if (string.IsNullOrWhiteSpace(domainConfig.SubDomain)) return;
            Console.WriteLine($"- Check: {domainConfig.SubDomain}");

            var client = new CloudFlareClient(FullConfig.ApiToken);
            var haName = string.IsNullOrWhiteSpace(domainConfig.HADomain)
                ? $"_ha.{domainConfig.SubDomain}"
                : domainConfig.HADomain;

            var dnsRecords = (await client.Zones.DnsRecords.GetAsync(domainConfig.ZoneId,
                new DnsRecordFilter() { Name = domainConfig.SubDomain })).Result;
            var haRecords = (await client.Zones.DnsRecords.GetAsync(domainConfig.ZoneId,
                new DnsRecordFilter() { Name = haName })).Result;

            if (!haRecords.Any())
            {
                Console.WriteLine($"    ⚠  HA NotFound: {haName} / {domainConfig.SubDomain}");
                return;
            }

            var accessibleRecords = new List<DnsRecord>();
            foreach (var record in haRecords)
            {
                if (await IsRecordAccessible(record))
                {
                    accessibleRecords.Add(record);
                    Console.WriteLine($"  - ✓ {record.Name} ({record.Content}) UP");
                }
                else Console.WriteLine($"  - ✗ {record.Name} ({record.Content}) DOWN");
            }

            if (!accessibleRecords.Any())
            {
                Console.WriteLine($"    ⚠  No Accessible HA: {haName} / {domainConfig.SubDomain}");
                return;
            }

            var bestRecord = accessibleRecords.OrderByDescending(r => r.Ttl).First();

            var mainRecord = dnsRecords
                .FirstOrDefault(r => r.Name == domainConfig.SubDomain &&
                                     r.Type is DnsRecordType.A or DnsRecordType.Cname);

            if (mainRecord != null &&
                mainRecord.Content == bestRecord.Content &&
                mainRecord.Type == bestRecord.Type)
            {
                Console.WriteLine($"    No Update Needed : {domainConfig.SubDomain} / {bestRecord.Content}");
                return;
            }

            if (mainRecord != null)
            {
                await client.Zones.DnsRecords.DeleteAsync(domainConfig.ZoneId, mainRecord.Id);
                Console.WriteLine($"    - Deleted Old Record : {domainConfig.SubDomain}");
            }

            var newRecord = new NewDnsRecord()
            {
                Name = domainConfig.SubDomain,
                Type = bestRecord.Type,
                Content = bestRecord.Content,
                Ttl = bestRecord.Ttl,
                Proxied = bestRecord.Proxied
            };

            await client.Zones.DnsRecords.AddAsync(domainConfig.ZoneId, newRecord);
            Console.WriteLine($"    - Updated {domainConfig.SubDomain} : {bestRecord.Content} ({bestRecord.Type})");
        }

        static async Task<bool> IsRecordAccessible(DnsRecord record)
        {
            try
            {
                IPAddress[] addresses;

                switch (record.Type)
                {
                    case DnsRecordType.Cname:
                    {
                        var hostEntry = await Dns.GetHostEntryAsync(record.Content);
                        addresses = hostEntry.AddressList;
                        break;
                    }
                    case DnsRecordType.A:
                        addresses = [IPAddress.Parse(record.Content)];
                        break;
                    default:
                        return false;
                }

                if (!addresses.Any()) return false;
                for (var i = 0; i < FullConfig.Retries; i++)
                {
                    if (await Tcping(addresses.First(), FullConfig.CheckPort, FullConfig.Timeout))
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

        static async Task<bool> Tcping(IPAddress ip, int port, int timeoutMs)
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
    }

    public class Config
    {
        public string ApiToken { get; set; }
        public int CheckInterval { get; set; } = 60 * 1000; // 60s
        public int Timeout { get; set; } = 1000; // 1s
        public int CheckPort { get; set; } = 80;
        public int Retries { get; set; } = 4;
        public List<DomainConfig> Domains { get; set; }
    }

    public class DomainConfig
    {
        public string? HADomain { get; set; } = string.Empty;
        public string SubDomain { get; set; }
        public string ZoneId { get; set; }
    }
}