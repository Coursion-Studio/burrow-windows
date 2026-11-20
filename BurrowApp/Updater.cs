using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Burrow
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Url, Sha256, Signature, Published;
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Over-the-air updates. The feed (update.json, attached to every release) says which version is newest
    /// and where its installer is. An installer is only run if its SHA-256 matches the feed AND it carries a
    /// valid RSA signature from the Burrow update key (public half in UpdateKey.cs).
    /// Nothing installs without the user's OK.
    /// </summary>
    public static class Updater
    {
        /// <summary>Every release replaces update.json, so this URL always points at the newest one.</summary>
        public const string DefaultFeed = "https://github.com/Coursion-Studio/burrow-windows/releases/latest/download/update.json";

        public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version);
        public static string CurrentText => Text(Current);
        public static string Text(Version v) => v.Major + "." + v.Minor + "." + v.Build;

        static Version Norm(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        static HttpClient Client()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Burrow-Windows/" + CurrentText);
            return c;
        }

        /// <summary>Reads the feed and returns it (whatever its version). Throws on network or feed errors.</summary>
        public static async Task<UpdateInfo> ReadFeedAsync(string feedUrl)
        {
            string json;
            var uri = new Uri(feedUrl);
            if (uri.IsFile) json = await File.ReadAllTextAsync(uri.LocalPath);   // used by tests
            else using (var c = Client()) json = await c.GetStringAsync(uri);

            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var info = new UpdateInfo
            {
                Version = Norm(new Version(S("version") ?? throw new InvalidDataException(Loc.T("err.upd.feed")))),
                Url = S("url"), Sha256 = S("sha256"), Signature = S("signature"), Published = S("published"),
            };
            if (r.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                foreach (var n in notes.EnumerateArray()) if (n.ValueKind == JsonValueKind.String) info.Notes.Add(n.GetString());
            if (info.Url == null || info.Sha256 == null || info.Signature == null)
                throw new InvalidDataException(Loc.T("err.upd.feed"));
            return info;
        }

        /// <summary>Returns the update if the feed has a newer version than <paramref name="current"/>, otherwise null.</summary>
        public static async Task<UpdateInfo> CheckAsync(string feedUrl, Version current = null)
        {
            var info = await ReadFeedAsync(feedUrl);
            return info.Version > Norm(current ?? Current) ? info : null;
        }

        /// <summary>Downloads the installer to %TEMP% and verifies it. Returns its path, or throws if anything doesn't match.</summary>
        public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<int> progress)
        {
            string dir = Path.Combine(Path.GetTempPath(), "Burrow-update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "BurrowSetup-" + Text(u.Version) + ".exe");
            if (File.Exists(path)) File.Delete(path);

            var uri = new Uri(u.Url);
            if (uri.IsFile) { File.Copy(uri.LocalPath, path); progress?.Report(100); }
            else
            {
                using var c = Client();
                using var resp = await c.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
                await using (var src = await resp.Content.ReadAsStreamAsync())
                await using (var dst = File.Create(path))
                {
                    var buf = new byte[81920];
                    int n, last = -1;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        if (total > 0) { int pct = (int)(done * 100 / total); if (pct != last) { last = pct; progress?.Report(pct); } }
                    }
                }
            }

            try { Verify(await File.ReadAllBytesAsync(path), u); }
            catch { try { File.Delete(path); } catch { } throw; }
            return path;
        }

        /// <summary>Throws CryptographicException unless the bytes match the feed's checksum AND signature.</summary>
        public static void Verify(byte[] data, UpdateInfo u)
        {
            string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (!string.Equals(hash, u.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException(Loc.T("err.upd.checksum"));

            byte[] sig;
            try { sig = Convert.FromBase64String(u.Signature); }
            catch (FormatException) { throw new CryptographicException(Loc.T("err.upd.badsig")); }

            using var rsa = RSA.Create();
            rsa.FromXmlString(UpdateKey.PublicXml);
            if (!rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException(Loc.T("err.upd.signature"));
        }

        /// <summary>
        /// Starts the verified installer silently. It closes this copy, updates in place, keeps Start with Windows
        /// as it is, and reopens the app. The caller should quit right after.
        /// </summary>
        public static void StartInstaller(string setupPath)
        {
            bool startup = false;
            try { startup = StartupTask.Exists(); } catch { }
            Process.Start(new ProcessStartInfo(setupPath, "/S /launch" + (startup ? " /startup" : "")) { UseShellExecute = true });
        }

        public static string Friendly(Exception ex)
        {
            var inner = ex is AggregateException ? ex.GetBaseException() : ex;
            if (inner is HttpRequestException || inner is TaskCanceledException)
                return Loc.T("err.upd.offline");
            return inner.Message;
        }
    }
}
