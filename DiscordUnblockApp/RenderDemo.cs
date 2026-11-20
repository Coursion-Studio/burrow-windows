#if RENDER
// Dev-only: renders README screenshots from the real window with demo data (no network, no driver, no personal data).
// Only compiled with -p:DefineConstants=RENDER (see tools\render-screens.ps1); never part of the shipped app.
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiscordUnblock
{
    public partial class MainWindow
    {
        public static async Task RenderAllAsync(string outDir)
        {
            Directory.CreateDirectory(outDir);

            var on = new MainWindow();
            await on.ShowOffscreenAsync();
            on.DemoCommon();
            on.SetState(UiState.On);
            int i = 0;
            foreach (var ms in new[] { 142, 96, 118 }) SetProbe(on._probes[i++], CGood, $"reachable · {ms} ms", CGood);
            on.DemoLog(
                "GoodbyeDPI is installed in C:\\ProgramData\\DiscordUnblock",
                "Starting: goodbyedpi -6 --dns-addr 77.88.8.8 --dns-port 1253 --blacklist discord-domains.txt",
                "Bypass is on. Fully restart Discord (including its tray icon) if it's open.",
                "Connection check: every Discord service is reachable.");
            await on.SaveAsync(Path.Combine(outDir, "shot-main.png"));

            var find = new MainWindow();
            await find.ShowOffscreenAsync();
            find.DemoCommon();
            find.SetBusy(true, "Finding the best method…");
            find.StatusSub.Text = "Testing method 2 of 5: Auto-TTL fake";
            foreach (var r in find._probes) SetProbe(r, CBusy, "checking…", CSub);
            find.DemoLog(
                "Scoring each method with 12 real connections to Discord…",
                "Standard: 9/12 connections got through (75%).");
            await find.SaveAsync(Path.Combine(outDir, "shot-findbest.png"));

            var off = new MainWindow();
            await off.ShowOffscreenAsync();
            off.DemoCommon();
            off.SetState(UiState.Off);
            off.DemoLog("GoodbyeDPI is installed in C:\\ProgramData\\DiscordUnblock", "Bypass is off.");
            await off.SaveAsync(Path.Combine(outDir, "shot-off.png"));
        }

        async Task ShowOffscreenAsync()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -8000; Top = 0;
            ShowActivated = false;
            Show();
            await Task.Delay(300);
        }

        void DemoCommon()
        {
            _loading = true;
            StartupSwitch.IsChecked = true;
            DiscordOnlySwitch.IsChecked = true;
            DnsSwitch.IsChecked = true;
            SelectMethodRow(1);
            _loading = false;
        }

        void DemoLog(params string[] lines)
        {
            var t = new DateTime(2026, 11, 20, 18, 47, 50);
            LogBox.Clear();
            foreach (var l in lines) { LogBox.AppendText($"{t:HH:mm:ss}  {l}\n"); t = t.AddSeconds(3); }
        }

        async Task SaveAsync(string file)
        {
            await Task.Delay(900);   // let colour transitions finish
            UpdateLayout();
            var rtb = new RenderTargetBitmap((int)(ActualWidth * 2), (int)(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            rtb.Render(this);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(file)) enc.Save(f);
            _exiting = true;
            _tray.Visible = false;
            _tray.Dispose();
            Close();
        }
    }
}
#endif
