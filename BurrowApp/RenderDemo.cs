#if RENDER
// Dev-only: renders README screenshots from the real window with demo data (no network, no driver, no personal data).
// Only compiled with -p:DefineConstants=RENDER (see tools\render-screens.ps1); never part of the shipped app.
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Burrow
{
    public partial class MainWindow
    {
        public static async Task RenderAllAsync(string outDir)
        {
            Directory.CreateDirectory(outDir);
            string sfx = Loc.Current == "en" ? "" : "." + Loc.Current;   // shot-main.png, shot-main.tr.png …

            var on = new MainWindow();
            await on.ShowOffscreenAsync();
            on.DemoCommon();
            on.SetState(UiState.On);
            int i = 0;
            foreach (var ms in new[] { 142, 96, 118 }) SetProbe(on._probes[i++], CGood, Loc.T("probe.ok", ms), CGood);
            on.RestartDiscordPanel.Visibility = Visibility.Visible;
            on.DemoLog(Loc.T("log.gdpi.ready"), Loc.T("log.dns.using", "AdGuard"), Loc.T("log.on"),
                       Loc.T("log.restartHint"), Loc.T("log.check.ok"));
            await on.SaveAsync(Path.Combine(outDir, "shot-main" + sfx + ".png"));

            var find = new MainWindow();
            await find.ShowOffscreenAsync();
            find.DemoCommon();
            find.SetBusy(true, Loc.T("busy.findbest"));
            find.StatusSub.Text = Loc.T("findbest.testing", 2, 5, Methods.Get(2).Name);
            foreach (var r in find._probes) SetProbe(r, CBusy, Loc.T("probe.checking"), CSub);
            find.DemoLog(Loc.T("log.findbest.start", 12), Loc.T("log.findbest.score", Methods.Get(1).Name, 9, 12, 75));
            await find.SaveAsync(Path.Combine(outDir, "shot-findbest" + sfx + ".png"));

            var upd = new MainWindow();
            await upd.ShowOffscreenAsync();
            upd.DemoCommon();
            upd.SetState(UiState.On);
            i = 0;
            foreach (var ms in new[] { 131, 102, 124 }) SetProbe(upd._probes[i++], CGood, Loc.T("probe.ok", ms), CGood);
            upd.DemoLog(Loc.T("log.on"), Loc.T("log.upd.available", "1.4.0"));
            var demo = new UpdateInfo { Version = new Version(1, 4, 0), Published = "2026-12-04" };
            if (Loc.Current == "tr")
            {
                demo.Notes.Add("Daha hızlı başlangıç. Güç düğmesine bastıktan sonra engel aşma bir saniye kadar daha erken hazır.");
                demo.Notes.Add("En iyi yöntemi bul artık daha sert engelleyen sağlayıcılar için birleşik yöntemleri de deniyor.");
            }
            else if (Loc.Current == "ru")
            {
                demo.Notes.Add("Быстрее запуск. Обход готов примерно на секунду раньше после нажатия кнопки.");
                demo.Notes.Add("«Найти лучший способ» теперь пробует и комбинированные способы для провайдеров со строгой блокировкой.");
            }
            else
            {
                demo.Notes.Add("Faster start. The bypass is ready about a second sooner after you click the power button.");
                demo.Notes.Add("Find best method now also tries combined methods for providers that block more aggressively.");
            }
            upd.ShowUpdate(demo);
            await upd.SaveAsync(Path.Combine(outDir, "shot-update" + sfx + ".png"));

            var off = new MainWindow();
            await off.ShowOffscreenAsync();
            off.DemoCommon();
            off.SetState(UiState.Off);
            off.DemoLog(Loc.T("log.gdpi.ready"), Loc.T("log.off"));
            await off.SaveAsync(Path.Combine(outDir, "shot-off" + sfx + ".png"));
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
            AutoUpdateSwitch.IsChecked = true;   // the real default
            DnsText.Text = DnsChoice.Label("auto", DnsChoice.Get("adguard"));
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
