using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Burrow
{
    public sealed class Method
    {
        public int Id { get; }
        public string Args { get; }
        public string Name => Loc.T($"method.{Id}.name");
        public string Description => Loc.T($"method.{Id}.desc");
        public Method(int id, string args) { Id = id; Args = args; }
    }

    public static class Methods
    {
        // Order = most reliable first (measured: -6, -5 and -1 got 100% of Discord handshakes through,
        // -9 about 53%, a plain SNI split 0%).
        public static readonly Method[] All =
        {
            new Method(1, "-6"),   // Standard: split handshake + wrong-seq decoys
            new Method(2, "-5"),   // Auto-TTL decoys
            new Method(3, "-1"),   // Legacy
            new Method(4, "-9"),   // Aggressive: every decoy type
            new Method(5, "-7"),   // Wrong checksum decoys
        };

        public static Method Get(int id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];
    }

    public sealed class Settings
    {
        public int Method { get; set; } = 1;
        public bool DiscordOnly { get; set; } = true;
        public bool FixDns { get; set; } = true;           // pre-1.3 setting; read once into Dns
        public string Dns { get; set; }                    // "auto", "off", or a DnsChoice id
        public bool WasOn { get; set; }
        public bool TrayHintShown { get; set; }
        public bool FirstRunDone { get; set; }
        public string Language { get; set; } = "auto";    // "auto" = Windows display language

        // Updates
        public bool AutoUpdate { get; set; } = true;
        public long LastUpdateCheck { get; set; }          // UTC ticks
        public string SkippedVersion { get; set; }
        public string UpdateFeed { get; set; }             // blank = the public feed (override only for testing)

        [System.Text.Json.Serialization.JsonIgnore] public bool IsNew { get; private set; }

        static string FilePath => Path.Combine(Engine.Dir, "settings.json");

        public static Settings Load()
        {
            Settings s = null;
            try
            {
                if (File.Exists(FilePath))
                    s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
            }
            catch { }
            if (s == null) s = new Settings { IsNew = true };
            if (string.IsNullOrEmpty(s.Dns)) s.Dns = s.FixDns ? "auto" : "off";
            if (!s.IsNew && !File.Exists(FilePath)) s.IsNew = true;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Engine.Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            }
            catch { }
        }
    }

    /// <summary>Downloads (pinned + verified), starts and stops GoodbyeDPI.</summary>
    public sealed class Engine
    {
        public static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Burrow");
        static readonly string LegacyDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiscordUnblock");
        const string LegacyServiceName = "DiscordUnblock";   // only the very first script version used a service

        // ---------- GoodbyeDPI, pinned ----------
        // A fixed release, checked by SHA-256. GitHub publishes no checksums for it, so these were recorded from the
        // official release when it was pinned. Anything that doesn't match is refused and fetched again.
        const string GdpiVersion = "0.2.3rc3";
        const string GdpiUrl = "https://github.com/ValdikSS/GoodbyeDPI/releases/download/0.2.3rc3/goodbyedpi-0.2.3rc3-2.zip";
        const string GdpiZipSha256 = "37f96b32d050dadcc930a639eba68e1ccd57ed5c04a5f77dfca908f01905a4c5";
        static readonly Dictionary<string, string> GdpiFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["goodbyedpi.exe"]  = "8d412b094bb9c137ff25ba9a794d1122ecc84bb776debff6c249723a13cc31cd",
            ["WinDivert.dll"]   = "6110bfa44667405179c3e15e12af1b62037e447ed59b054b19042032995e6c7e",
            ["WinDivert64.sys"] = "e69b5ba3f0cd6cfb2983e442636e7f0b342b61b15264b0328317d4559c82cf50",
        };

        static readonly string[] DiscordDomains =
        {
            "discord.com", "discordapp.com", "discord.gg", "discord.media", "discordapp.net",
            "discord.dev", "discord.new", "discord.gift", "discord.co", "discordcdn.com",
            "discordstatus.com", "dis.gd", "discord-attachments-uploads-prd.storage.googleapis.com",
        };

        public static readonly (string Key, string Url)[] Probes =
        {
            ("probe.api",     "https://discord.com/api/v9/gateway"),
            ("probe.gateway", "https://gateway.discord.gg"),
            ("probe.media",   "https://cdn.discordapp.com"),
        };

        public event Action<string> Log;
        /// <summary>GoodbyeDPI stopped without the app asking it to (crash, killed by antivirus…).</summary>
        public event Action UnexpectedExit;
        void L(string s) => Log?.Invoke(s);

        public string Exe => Path.Combine(Dir, "goodbyedpi.exe");
        public bool IsInstalled => GdpiFiles.Keys.All(f => File.Exists(Path.Combine(Dir, f)));

        /// <summary>The DNS server actually used by the last start (resolved from "auto").</summary>
        public DnsChoice ActiveDns { get; private set; }

        public bool IsRunning
        {
            get
            {
                var ps = Process.GetProcessesByName("goodbyedpi");
                foreach (var p in ps) p.Dispose();
                return ps.Length > 0;
            }
        }

        // ---------- migration from DiscordUnblock (≤ 1.2) ----------

        /// <summary>Moves settings and downloads from ProgramData\DiscordUnblock. Call before Settings.Load().</summary>
        public static void MigrateLegacyData()
        {
            if (Directory.Exists(Dir) || !Directory.Exists(LegacyDir)) return;
            try { Directory.Move(LegacyDir, Dir); return; } catch { }
            try
            {
                Directory.CreateDirectory(Dir);
                string s = Path.Combine(LegacyDir, "settings.json");
                if (File.Exists(s)) File.Copy(s, Path.Combine(Dir, "settings.json"), true);
                Directory.Delete(LegacyDir, true);
            }
            catch { }
        }

        // ---------- install (pinned + verified) ----------

        static string Sha256Of(string path)
        {
            using var f = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        }

        /// <summary>True only if every GoodbyeDPI file is present and matches its pinned hash.</summary>
        public bool IsVerified()
        {
            try { return GdpiFiles.All(kv => File.Exists(Path.Combine(Dir, kv.Key)) && Sha256Of(Path.Combine(Dir, kv.Key)) == kv.Value); }
            catch { return false; }
        }

        public async Task EnsureInstalledAsync()
        {
            if (IsVerified()) return;
            if (IsInstalled) L(Loc.T("log.gdpi.mismatch"));
            L(Loc.T("log.gdpi.downloading", GdpiVersion));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Burrow/" + Updater.CurrentText);
            byte[] zipBytes = await http.GetByteArrayAsync(GdpiUrl);
            string got = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
            if (got != GdpiZipSha256) throw new Exception(Loc.T("err.gdpi.hash"));

            await KillAllAsync();
            Directory.CreateDirectory(Dir);
            using (var zip = new ZipArchive(new MemoryStream(zipBytes)))
            {
                foreach (var e in zip.Entries)
                {
                    var parts = e.FullName.Replace('\\', '/').Split('/');
                    if (parts.Length < 2 || parts[^2] != "x86_64" || !GdpiFiles.ContainsKey(e.Name)) continue;
                    string dest = Path.Combine(Dir, e.Name);
                    try { e.ExtractToFile(dest, true); }
                    catch (IOException)
                    {
                        // The driver file stays locked while WinDivert is loaded: unload it and try again.
                        await RunAsync("sc.exe", "stop", "WinDivert");
                        await Task.Delay(500);
                        e.ExtractToFile(dest, true);
                    }
                }
            }
            if (!IsVerified()) throw new Exception(Loc.T("err.gdpi.hash"));
            L(Loc.T("log.gdpi.installed", GdpiVersion));
        }

        // ---------- arguments ----------

        public List<string> BuildArgs(Settings s, DnsChoice dns)
        {
            var a = Methods.Get(s.Method).Args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (dns != null)
            {
                a.AddRange(new[] { "--dns-addr", dns.V4, "--dns-port", dns.Port.ToString() });
                if (dns.V6 != null) a.AddRange(new[] { "--dnsv6-addr", dns.V6, "--dnsv6-port", dns.Port6.ToString() });
            }
            if (s.DiscordOnly)
            {
                string list = Path.Combine(Dir, "discord-domains.txt");
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(list, DiscordDomains);
                a.Add("--blacklist");
                a.Add(list);
            }
            return a;
        }

        static string Q(string s) => s.Contains(' ') || s.Contains('\\') ? "\"" + s + "\"" : s;

        // ---------- process ----------

        Process _proc;

        public async Task StartProcessAsync(Settings s)
        {
            await KillAllAsync();
            ActiveDns = await DnsChoice.ResolveAsync(s.Dns);
            if (ActiveDns != null) L(Loc.T("log.dns.using", ActiveDns.Name));
            else if (s.Dns != "off") L(Loc.T("log.dns.none"));

            var psi = new ProcessStartInfo(Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Dir,
            };
            foreach (var x in BuildArgs(s, ActiveDns)) psi.ArgumentList.Add(x);
            L(Loc.T("log.starting") + " goodbyedpi " + string.Join(" ", psi.ArgumentList.Select(Q)));

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) L("  " + e.Data.Trim()); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) L("  " + e.Data.Trim()); };
            p.Exited += (_, __) =>
            {
                if (!ReferenceEquals(_proc, p)) return;   // we stopped it on purpose
                _proc = null;
                L(Loc.T("log.gdpi.exited", SafeExitCode(p)));
                UnexpectedExit?.Invoke();
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Job.Attach(p);   // dies with this app, even if the app crashes
            _proc = p;
        }

        static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

        public async Task KillAllAsync()
        {
            _proc = null;
            foreach (var p in Process.GetProcessesByName("goodbyedpi"))
            {
                try { p.Kill(); await p.WaitForExitAsync(); } catch { }
                finally { p.Dispose(); }
            }
        }

        // ---------- legacy service (first script version only) ----------

        public bool LegacyServiceExists() => RunSync("sc.exe", "query", LegacyServiceName) != 1060;

        public async Task RemoveLegacyServiceAsync()
        {
            if (!LegacyServiceExists()) return;
            await RunAsync("sc.exe", "stop", LegacyServiceName);
            for (int i = 0; i < 25 && IsRunning; i++) await Task.Delay(200);
            await RunAsync("sc.exe", "delete", LegacyServiceName);
        }

        public async Task StopAllAsync()
        {
            if (LegacyServiceExists()) await RunAsync("sc.exe", "stop", LegacyServiceName);
            await KillAllAsync();
        }

        // ---------- helpers ----------

        public static Task FlushDnsAsync() => RunAsync("ipconfig.exe", "/flushdns");

        public static async Task<(int Code, string Output)> RunAsync(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().ConfigureAwait(false);
            return (p.ExitCode, await o.ConfigureAwait(false) + await e.ConfigureAwait(false));
        }

        static int RunSync(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }

        /// <summary>
        /// Runs <paramref name="count"/> fresh TLS handshakes against Discord hosts and returns how many succeeded.
        /// This is exactly what DPI interferes with, so it's a far better score than a few HTTP requests.
        /// </summary>
        public static async Task<int> HandshakeScoreAsync(int count)
        {
            string[] hosts = { "discord.com", "cdn.discordapp.com", "gateway.discord.gg" };
            int ok = 0;
            for (int start = 0; start < count; start += 6)
            {
                var batch = Enumerable.Range(start, Math.Min(6, count - start))
                                      .Select(i => HandshakeAsync(hosts[i % hosts.Length]));
                ok += (await Task.WhenAll(batch)).Count(x => x);
            }
            return ok;
        }

        static async Task<bool> HandshakeAsync(string host)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(host, 443, cts.Token);
                using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, __, ___, ____) => true);
                await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Is the internet up at all? (Plain TCP to two well-known non-Discord servers.) Used so self-healing
        /// doesn't mistake "Wi-Fi is down" for "Discord is blocked".
        /// </summary>
        public static async Task<bool> InternetUpAsync()
        {
            async Task<bool> Tcp(string ip)
            {
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
                    using var tcp = new System.Net.Sockets.TcpClient();
                    await tcp.ConnectAsync(System.Net.IPAddress.Parse(ip), 443, cts.Token);
                    return true;
                }
                catch { return false; }
            }
            var r = await Task.WhenAll(Tcp("1.1.1.1"), Tcp("8.8.8.8"));
            return r.Any(x => x);
        }

        /// <summary>Any HTTP response at all means the server is reachable (not blocked).</summary>
        public static async Task<(bool Ok, long Ms, string Detail)> ProbeAsync(string url)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var h = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(6),
                    PooledConnectionLifetime = TimeSpan.Zero,
                    AllowAutoRedirect = false,
                };
                using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(8) };
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Burrow/" + Updater.CurrentText);
                using var r = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                return (true, sw.ElapsedMilliseconds, $"HTTP {(int)r.StatusCode}");
            }
            catch (TaskCanceledException) { return (false, sw.ElapsedMilliseconds, Loc.T("probe.timeout")); }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                string msg = inner.Message.TrimEnd('.');
                if (msg.Length > 40) msg = msg.Substring(0, 40) + "…";
                return (false, sw.ElapsedMilliseconds, msg);
            }
        }
    }

    /// <summary>Windows Job object so GoodbyeDPI is killed when the app exits for any reason.</summary>
    static class Job
    {
        static IntPtr _handle;

        public static void Attach(Process p)
        {
            try
            {
                if (_handle == IntPtr.Zero)
                {
                    _handle = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                    SetInformationJobObject(_handle, 9, ref info, (uint)Marshal.SizeOf(info));
                }
                AssignProcessToJobObject(_handle, p.Handle);
            }
            catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint len);
        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                         ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }
}
