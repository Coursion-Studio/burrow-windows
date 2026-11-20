using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Burrow
{
    /// <summary>
    /// Where GoodbyeDPI sends DNS lookups. Internet providers that block Discord often also answer DNS with a fake
    /// address (a block page), so lookups are redirected to a public resolver. Windows settings aren't touched.
    /// </summary>
    public sealed class DnsChoice
    {
        public string Id { get; }
        public string Name { get; }
        public string V4 { get; }
        public int Port { get; }
        public string V6 { get; }
        public int Port6 { get; }

        DnsChoice(string id, string name, string v4, int port, string v6, int port6)
        { Id = id; Name = name; V4 = v4; Port = port; V6 = v6; Port6 = port6; }

        /// <summary>Most private first. "Auto" uses the first one that returns Discord's real addresses.</summary>
        public static readonly DnsChoice[] All =
        {
            new DnsChoice("adguard",    "AdGuard",    "94.140.14.14", 5353, "2a10:50c0::ad1:ff",    5353),
            new DnsChoice("quad9",      "Quad9",      "9.9.9.9",      53,   "2620:fe::fe",          53),
            new DnsChoice("cloudflare", "Cloudflare", "1.1.1.1",      53,   "2606:4700:4700::1111", 53),
            new DnsChoice("google",     "Google",     "8.8.8.8",      53,   "2001:4860:4860::8888", 53),
            new DnsChoice("yandex",     "Yandex",     "77.88.8.8",    1253, "2a02:6b8::feed:0ff",   1253),
        };

        public static DnsChoice Get(string id) => All.FirstOrDefault(d => d.Id == id);

        /// <summary>The label shown in Options for a setting value ("auto", "off" or an id).</summary>
        public static string Label(string id, DnsChoice active = null)
        {
            if (id == "off") return Loc.T("dns.off");
            if (id == "auto") return active != null ? Loc.T("dns.auto.using", active.Name) : Loc.T("dns.auto");
            return Get(id)?.Name ?? Loc.T("dns.auto");
        }

        /// <summary>null = don't redirect DNS.</summary>
        public static async Task<DnsChoice> ResolveAsync(string setting)
        {
            if (setting == "off") return null;
            var fixedChoice = Get(setting);
            if (fixedChoice != null) return fixedChoice;
            foreach (var d in All)
                if (await GivesRealDiscordAsync(d)) return d;
            return All[0];   // nothing answered (offline?): use the most private one
        }

        /// <summary>Asks the server for discord.com directly and checks the answer is Discord's real network.</summary>
        public static async Task<bool> GivesRealDiscordAsync(DnsChoice d)
        {
            try
            {
                var ips = await QueryAAsync(d.V4, d.Port, "discord.com", 2000);
                return ips.Any(IsDiscordAddress);
            }
            catch { return false; }
        }

        // Discord is served from Cloudflare's 162.159.0.0/16; block pages never are.
        static bool IsDiscordAddress(byte[] ip) => ip.Length == 4 && ip[0] == 162 && ip[1] == 159;

        static async Task<List<byte[]>> QueryAAsync(string server, int port, string name, int timeoutMs)
        {
            var q = new List<byte> { 0x42, 0x55, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            foreach (var label in name.Split('.')) { q.Add((byte)label.Length); foreach (char ch in label) q.Add((byte)ch); }
            q.AddRange(new byte[] { 0, 0, 1, 0, 1 });   // QTYPE A, QCLASS IN

            using var udp = new UdpClient();
            await udp.SendAsync(q.ToArray(), q.Count, server, port);
            var receive = udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(timeoutMs)) != receive) throw new TimeoutException();
            var r = receive.Result.Buffer;

            var ips = new List<byte[]>();
            int answers = (r[6] << 8) | r[7];
            int i = 12;
            while (r[i] != 0) i += r[i] + 1;   // skip question name
            i += 5;
            for (int a = 0; a < answers && i < r.Length; a++)
            {
                if ((r[i] & 0xC0) == 0xC0) i += 2; else { while (r[i] != 0) i += r[i] + 1; i++; }
                int type = (r[i] << 8) | r[i + 1];
                int len = (r[i + 8] << 8) | r[i + 9];
                i += 10;
                if (type == 1 && len == 4) ips.Add(new[] { r[i], r[i + 1], r[i + 2], r[i + 3] });
                i += len;
            }
            return ips;
        }
    }
}
