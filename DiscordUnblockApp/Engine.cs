using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace DiscordUnblock
{
    public sealed class Method
    {
        public int Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Args { get; }
        public Method(int id, string name, string description, string args)
        { Id = id; Name = name; Description = description; Args = args; }
    }

    public static class Methods
    {
        public static readonly Method[] All =
        {
            // Order = most reliable first (measured: -6, -5 and -1 got 100% of Discord handshakes through,
            // -9 about 53%, a plain SNI split 0%).
            new Method(1, "Standard",       "Split handshake + decoys the server ignores. Most reliable.", "-6"),
            new Method(2, "Auto-TTL fake",  "Decoy packets that expire before reaching the server.",       "-5"),
            new Method(3, "Legacy",         "Old-style tricks for older DPI systems. A bit slower.",       "-1"),
            new Method(4, "Aggressive",     "Every decoy type at once. Helps on some ISPs, hurts on others.", "-9"),
            new Method(5, "Wrong checksum", "Decoys with a broken checksum that only DPI reads.",           "-7"),
        };

        public static Method Get(int id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];
    }

    public sealed class Settings
    {
        public int Method { get; set; } = 1;
        public bool DiscordOnly { get; set; } = true;
        public bool FixDns { get; set; } = true;
        public bool WasOn { get; set; }
        public bool TrayHintShown { get; set; }

        // Updates
        public bool AutoUpdate { get; set; } = true;
        public long LastUpdateCheck { get; set; }          // UTC ticks
        public string SkippedVersion { get; set; }
        public string UpdateFeed { get; set; }             // blank = the public feed (override only for testing)

        static string FilePath => Path.Combine(Engine.Dir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch { }
            return new Settings();
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

    /// <summary>Downloads, starts and stops GoodbyeDPI, either as a child process or as a Windows service.</summary>
    public sealed class Engine
    {
        public static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiscordUnblock");
        public const string ServiceName = "DiscordUnblock";

        static readonly string[] DiscordDomains =
        {
            "discord.com", "discordapp.com", "discord.gg", "discord.media", "discordapp.net",
            "discord.dev", "discord.new", "discord.gift", "discord.co", "discordcdn.com",
            "discordstatus.com", "dis.gd", "discord-attachments-uploads-prd.storage.googleapis.com",
        };

        public static readonly (string Name, string Url)[] Probes =
        {
            ("Discord website & API", "https://discord.com/api/v9/gateway"),
            ("Real-time gateway",     "https://gateway.discord.gg"),
            ("Images & media",        "https://cdn.discordapp.com"),
        };

        public event Action<string> Log;
        void L(string s) => Log?.Invoke(s);

        public string Exe => Path.Combine(Dir, "goodbyedpi.exe");
        public bool IsInstalled => File.Exists(Exe) && File.Exists(Path.Combine(Dir, "WinDivert.dll"));

        public bool IsRunning
        {
            get
            {
                var ps = Process.GetProcessesByName("goodbyedpi");
                foreach (var p in ps) p.Dispose();
                return ps.Length > 0;
            }
        }

        // ---------- install ----------

        public async Task EnsureInstalledAsync()
        {
            if (IsInstalled) return;
            L("Downloading GoodbyeDPI from github.com/ValdikSS/GoodbyeDPI…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordUnblock/1.0");

            // Newest release including pre-releases: preset -9 only exists in 0.2.3+
            string json = await http.GetStringAsync("https://api.github.com/repos/ValdikSS/GoodbyeDPI/releases?per_page=5");
            string tag = null, url = null;
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    foreach (var a in rel.GetProperty("assets").EnumerateArray())
                    {
                        string n = a.GetProperty("name").GetString() ?? "";
                        string u = a.GetProperty("browser_download_url").GetString() ?? "";
                        if (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            u.StartsWith("https://github.com/ValdikSS/GoodbyeDPI/releases/download/", StringComparison.Ordinal))
                        {
                            tag = rel.GetProperty("tag_name").GetString();
                            url = u;
                            break;
                        }
                    }
                    if (url != null) break;
                }
            }
            if (url == null) throw new Exception("Couldn't find a GoodbyeDPI release on GitHub.");

            byte[] bytes = await http.GetByteArrayAsync(url);
            L($"Downloaded GoodbyeDPI {tag} ({bytes.Length / 1024} KB). Installing…");

            string arch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
            Directory.CreateDirectory(Dir);
            using (var zip = new ZipArchive(new MemoryStream(bytes)))
            {
                foreach (var e in zip.Entries)
                {
                    var parts = e.FullName.Replace('\\', '/').Split('/');
                    if (parts.Length >= 2 && parts[^2] == arch && e.Name.Length > 0)
                        e.ExtractToFile(Path.Combine(Dir, e.Name), true);
                }
            }
            if (!IsInstalled) throw new Exception($"The GoodbyeDPI zip didn't contain the {arch} build.");
            L($"Installed to {Dir}");
        }

        // ---------- arguments ----------

        public List<string> BuildArgs(Settings s)
        {
            var a = Methods.Get(s.Method).Args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (s.FixDns)
                a.AddRange(new[] { "--dns-addr", "77.88.8.8", "--dns-port", "1253",
                                   "--dnsv6-addr", "2a02:6b8::feed:0ff", "--dnsv6-port", "1253" });
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

        // ---------- child process mode ----------

        Process _proc;

        public async Task StartProcessAsync(Settings s)
        {
            await KillAllAsync();
            var psi = new ProcessStartInfo(Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Dir,
            };
            foreach (var x in BuildArgs(s)) psi.ArgumentList.Add(x);
            L("Starting: goodbyedpi " + string.Join(" ", psi.ArgumentList.Select(Q)));

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) L("  " + e.Data.Trim()); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) L("  " + e.Data.Trim()); };
            p.Exited += (_, __) => { if (ReferenceEquals(_proc, p)) { _proc = null; L($"GoodbyeDPI exited (code {SafeExitCode(p)})."); } };
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

        // ---------- service mode ----------

        public bool ServiceExists() => RunSync("sc.exe", "query", ServiceName) != 1060;

        public async Task InstallServiceAsync(Settings s, bool start)
        {
            await RemoveServiceAsync(quiet: true);
            await KillAllAsync();
            string bin = Q(Exe) + " " + string.Join(" ", BuildArgs(s).Select(Q));
            var r = await RunAsync("sc.exe", "create", ServiceName, "binPath=", bin, "start=", "auto",
                                   "DisplayName=", "Discord Unblock (GoodbyeDPI)");
            if (r.Code != 0) throw new Exception("Couldn't create the service: " + r.Output.Trim());
            await RunAsync("sc.exe", "description", ServiceName, "Bypasses DPI blocking for Discord. Managed by the DiscordUnblock app.");
            await RunAsync("sc.exe", "failure", ServiceName, "reset=", "0", "actions=", "restart/3000");
            L("Windows service installed (starts automatically at boot).");
            if (start) await StartServiceAsync();
        }

        public async Task StartServiceAsync()
        {
            var r = await RunAsync("sc.exe", "start", ServiceName);
            if (r.Code != 0 && r.Code != 1056 /* already running */)
                throw new Exception("Couldn't start the service: " + r.Output.Trim());
            L("Service started.");
        }

        public async Task StopServiceAsync()
        {
            await RunAsync("sc.exe", "stop", ServiceName);
            for (int i = 0; i < 25 && IsRunning; i++) await Task.Delay(200);
        }

        public async Task RemoveServiceAsync(bool quiet = false)
        {
            if (!ServiceExists()) return;
            await StopServiceAsync();
            await RunAsync("sc.exe", "delete", ServiceName);
            if (!quiet) L("Windows service removed.");
        }

        public async Task StopAllAsync()
        {
            if (ServiceExists()) await StopServiceAsync();
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
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 DiscordUnblock/1.0");
                using var r = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                return (true, sw.ElapsedMilliseconds, $"HTTP {(int)r.StatusCode}");
            }
            catch (TaskCanceledException) { return (false, sw.ElapsedMilliseconds, "timed out"); }
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
