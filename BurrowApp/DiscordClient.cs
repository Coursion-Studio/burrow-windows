using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Burrow
{
    /// <summary>
    /// Finds and restarts the Discord desktop app, so it reconnects through the bypass.
    /// Burrow runs as admin, but Discord must not: it's relaunched through Explorer, which starts it as the
    /// signed-in user, exactly like clicking its Start menu shortcut.
    /// </summary>
    public static class DiscordClient
    {
        static readonly string[] ProcessNames = { "Discord", "DiscordPTB", "DiscordCanary" };

        public static bool IsRunning => ProcessNames.Any(n => { var p = Process.GetProcessesByName(n); foreach (var x in p) x.Dispose(); return p.Length > 0; });

        /// <summary>Closes every Discord window and process, then opens Discord again. Returns false if it couldn't reopen it.</summary>
        public static async Task<bool> RestartAsync()
        {
            string flavour = null, exePath = null;
            foreach (var n in ProcessNames)
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { exePath ??= p.MainModule?.FileName; flavour ??= n; } catch { }
                    try { p.Kill(entireProcessTree: true); } catch { }
                    p.Dispose();
                }
            for (int i = 0; i < 20 && IsRunning; i++) await Task.Delay(250);
            await Task.Delay(400);
            return Launch(flavour ?? "Discord", exePath);
        }

        static bool Launch(string flavour, string exePath)
        {
            // 1. Discord's own Start menu shortcut (it carries the right updater arguments).
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string folder = Path.Combine(programs, "Discord Inc");
            string lnk = Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.lnk").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Replace(" ", "")
                      .Equals(flavour, StringComparison.OrdinalIgnoreCase)) ?? Directory.GetFiles(folder, "*.lnk").FirstOrDefault()
                : null;

            // 2. Otherwise a temporary shortcut to Discord's updater, which starts the newest version.
            if (lnk == null && exePath != null)
            {
                string root = Directory.GetParent(Path.GetDirectoryName(exePath))?.FullName;   // ...\Discord\app-1.0.x\Discord.exe
                string update = root == null ? null : Path.Combine(root, "Update.exe");
                lnk = Path.Combine(Path.GetTempPath(), "Burrow-open-discord.lnk");
                if (update != null && File.Exists(update)) MakeShortcut(lnk, update, "--processStart " + Path.GetFileName(exePath));
                else MakeShortcut(lnk, exePath, "");
            }
            if (lnk == null) return false;

            // Explorer opens it as the signed-in user, not as admin.
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + lnk + "\"") { UseShellExecute = false });
            return true;
        }

        static void MakeShortcut(string lnk, string target, string args)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
    }
}
