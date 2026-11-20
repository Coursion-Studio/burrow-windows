using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Burrow
{
    public partial class App : Application
    {
        const string MutexName = "Burrow.SingleInstance";
        const string ShowEventName = "Burrow.ShowWindow";

        static Mutex _single;
        static EventWaitHandle _showEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string Arg(string name) { int i = Array.FindIndex(e.Args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
            bool Has(string name) => e.Args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

#if RENDER
            if (Has("--render"))
            {
                Loc.Init(Arg("--lang") ?? "en");
                Dispatcher.InvokeAsync(async () =>
                {
                    await Burrow.MainWindow.RenderAllAsync(Arg("--render") ?? ".");
                    Shutdown();
                });
                return;
            }
#endif
            // Restarting to switch language: wait until the old copy has gone.
            if (int.TryParse(Arg("--after"), out int oldPid))
            {
                try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(10000); } catch { }
            }

            bool startInTray = Has("--tray");

            _single = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                // Already running (probably in the tray): ask that copy to show its window.
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }

            // Burrow used to be DiscordUnblock: bring its settings and downloads over before anything reads them.
            Engine.MigrateLegacyData();
            Loc.Init(Settings.Load().Language);

            DispatcherUnhandledException += (_, a) =>
            {
                MessageBox.Show(a.Exception.Message, Loc.T("app.error"), MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };

            var window = new MainWindow();
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    window.Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
            }) { IsBackground = true }.Start();

            window.Start(startInTray);
        }
    }
}
