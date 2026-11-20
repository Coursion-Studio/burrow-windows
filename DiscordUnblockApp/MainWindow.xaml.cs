using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace DiscordUnblock
{
    public partial class MainWindow : Window
    {
        enum UiState { Off, Busy, On, Error }

        sealed class ProbeRow { public string Url; public Ellipse Dot; public TextBlock Status; }

        static readonly Color CGray = (Color)ColorConverter.ConvertFromString("#3A3F4E");
        static readonly Color CSub = (Color)ColorConverter.ConvertFromString("#8B91A3");
        static readonly Color CGood = (Color)ColorConverter.ConvertFromString("#3DDC97");
        static readonly Color CBusy = (Color)ColorConverter.ConvertFromString("#F5B942");
        static readonly Color CBad = (Color)ColorConverter.ConvertFromString("#E5484D");

        readonly Engine _engine = new Engine();
        readonly Settings _s = Settings.Load();
        readonly List<ProbeRow> _probes = new List<ProbeRow>();
        readonly SolidColorBrush _ringBrush = new SolidColorBrush(CGray);
        readonly SolidColorBrush _iconBrush = new SolidColorBrush(CSub);

        WF.NotifyIcon _tray;
        WF.ToolStripMenuItem _trayStatus, _trayToggle;
        readonly Dictionary<UiState, SD.Icon> _trayIcons = new Dictionary<UiState, SD.Icon>();
        bool _busy, _loading = true, _exiting;
        UiState _state = UiState.Off;

        public MainWindow()
        {
            InitializeComponent();
            PowerRing.Stroke = _ringBrush;
            PowerIcon.Foreground = _iconBrush;
            _engine.Log += AppendLog;

            BuildMethodRows();
            BuildProbeRows();
            SetupTray();
            VersionText.Text = "v" + Updater.CurrentText;
        }

        /// <summary>Called by App. In tray mode the window stays hidden until the tray icon is clicked.</summary>
        public void Start(bool startInTray)
        {
            if (!startInTray) Show();
            _ = InitAsync(startInTray);
        }

        // ================= setup =================

        void BuildMethodRows()
        {
            foreach (var m in Methods.All)
            {
                var sp = new StackPanel();
                var title = new TextBlock { FontSize = 13, Foreground = (Brush)FindResource("Text") };
                title.Inlines.Add(new System.Windows.Documents.Run(m.Name) { FontWeight = FontWeights.SemiBold });
                if (m.Id == 1)
                    title.Inlines.Add(new System.Windows.Documents.Run("   recommended")
                    { FontSize = 11, Foreground = (Brush)FindResource("Accent") });
                sp.Children.Add(title);
                sp.Children.Add(new TextBlock
                {
                    Text = m.Description, FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0),
                    Foreground = (Brush)FindResource("SubText"), TextWrapping = TextWrapping.Wrap,
                });
                var rb = new RadioButton { Content = sp, GroupName = "method", Tag = m.Id, Style = (Style)FindResource("MethodRow") };
                rb.Checked += Method_Checked;
                MethodsPanel.Children.Add(rb);
            }
        }

        void BuildProbeRows()
        {
            foreach (var (name, url) in Engine.Probes)
            {
                var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(CGray), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                var n = new TextBlock { Text = name, FontSize = 12.5 };
                var st = new TextBlock { Text = "not checked", FontSize = 12, Foreground = new SolidColorBrush(CSub) };
                Grid.SetColumn(n, 1);
                Grid.SetColumn(st, 2);
                g.Children.Add(dot); g.Children.Add(n); g.Children.Add(st);
                ProbePanel.Children.Add(g);
                _probes.Add(new ProbeRow { Url = url, Dot = dot, Status = st });
            }
        }

        void SetupTray()
        {
            int px = Math.Max(16, WF.SystemInformation.SmallIconSize.Width);
            SD.Color Sd(Color c) => SD.Color.FromArgb(c.R, c.G, c.B);
            _trayIcons[UiState.On] = IconArt.ToIcon(IconArt.RenderTray(px, Sd(CGood)));
            _trayIcons[UiState.Off] = IconArt.ToIcon(IconArt.RenderTray(px, Sd(CSub)));
            _trayIcons[UiState.Busy] = IconArt.ToIcon(IconArt.RenderTray(px, Sd(CBusy)));
            _trayIcons[UiState.Error] = IconArt.ToIcon(IconArt.RenderTray(px, Sd(CBad)));

            var menu = new WF.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = new SD.Font("Segoe UI", 9.5f) };
            _trayStatus = new WF.ToolStripMenuItem("Off") { Enabled = false };
            _trayToggle = new WF.ToolStripMenuItem("Turn on", null, async (_, __) => await ToggleAsync());
            menu.Items.Add(_trayStatus);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(_trayToggle);
            menu.Items.Add(new WF.ToolStripMenuItem("Open DiscordUnblock", null, (_, __) => ShowFromTray()) { Font = new SD.Font("Segoe UI", 9.5f, SD.FontStyle.Bold) });
            menu.Items.Add(new WF.ToolStripMenuItem("Check for updates…", null, async (_, __) => { ShowFromTray(); await CheckForUpdatesAsync(manual: true); }));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(new WF.ToolStripMenuItem("Quit", null, async (_, __) => await ExitAppAsync()));
            foreach (WF.ToolStripItem i in menu.Items) { i.ForeColor = SD.Color.FromArgb(0xE9, 0xEB, 0xF1); i.Padding = new WF.Padding(4, 3, 4, 3); }

            _tray = new WF.NotifyIcon { Icon = _trayIcons[UiState.Off], Text = "DiscordUnblock: off", Visible = true, ContextMenuStrip = menu };
            _tray.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) ShowFromTray(); };
        }

        async Task InitAsync(bool startedInTray)
        {
            // Older versions used a Windows service; switch to the tray-app startup task.
            if (_engine.ServiceExists())
            {
                await _engine.RemoveServiceAsync(quiet: true);
                if (StartupTask.Create(Environment.ProcessPath))
                    AppendLog("Moved \"start with Windows\" from the old service to the tray app.");
            }

            StartupSwitch.IsChecked = StartupTask.Exists();
            DiscordOnlySwitch.IsChecked = _s.DiscordOnly;
            DnsSwitch.IsChecked = _s.FixDns;
            SelectMethodRow(_s.Method);
            InitUpdates();
            _loading = false;

            AppendLog(_engine.IsInstalled
                ? $"GoodbyeDPI is installed in {Engine.Dir}"
                : "GoodbyeDPI will be downloaded on first use (about 1 MB, from github.com/ValdikSS/GoodbyeDPI).");
            if (startedInTray) AppendLog("Started in the tray.");

            if (_engine.IsRunning)
            {
                AppendLog("The bypass is already running.");
                SetState(UiState.On);
                await TestAsync();
            }
            else if (_s.WasOn)
            {
                AppendLog("Turning the bypass back on (it was on last time).");
                await TurnOnAsync();
            }
            else SetState(UiState.Off);
        }

        // ================= actions =================

        async Task ToggleAsync()
        {
            if (_busy) return;
            if (_engine.IsRunning) await TurnOffAsync();
            else await TurnOnAsync();
        }

        async Task ApplyAsync()
        {
            await _engine.EnsureInstalledAsync();
            await _engine.StartProcessAsync(_s);
            await Engine.FlushDnsAsync();
            await Task.Delay(800);
            if (!_engine.IsRunning)
                throw new Exception("GoodbyeDPI stopped right after starting. Details are in Activity.");
        }

        async Task TurnOnAsync()
        {
            SetBusy(true, "Starting…");
            try
            {
                await ApplyAsync();
                _s.WasOn = true; _s.Save();
                SetBusy(false);
                SetState(UiState.On);
                AppendLog("Bypass is on. Fully restart Discord (including its tray icon) if it's open.");
                await TestAsync(retry: true);
            }
            catch (Exception ex) { Fail(ex); }
        }

        async Task TurnOffAsync()
        {
            SetBusy(true, "Stopping…");
            try
            {
                await _engine.StopAllAsync();
                await Engine.FlushDnsAsync();
                _s.WasOn = false; _s.Save();
                AppendLog("Bypass is off.");
                SetBusy(false);
                SetState(UiState.Off);
                foreach (var r in _probes) SetProbe(r, CGray, "not checked", CSub);
            }
            catch (Exception ex) { Fail(ex); }
        }

        async Task RestartIfRunningAsync()
        {
            if (!_engine.IsRunning) return;
            SetBusy(true, "Applying changes…");
            try
            {
                await ApplyAsync();
                SetBusy(false);
                SetState(UiState.On);
                await TestAsync(retry: true);
            }
            catch (Exception ex) { Fail(ex); }
        }

        void Fail(Exception ex)
        {
            AppendLog("Error: " + ex.Message);
            SetBusy(false);
            SetState(UiState.Error, ex.Message);
            if (!IsVisible)
                _tray.ShowBalloonTip(4000, "DiscordUnblock couldn't start", ex.Message, WF.ToolTipIcon.Warning);
        }

        async Task<bool> TestAsync(bool retry = false)
        {
            TestButton.IsEnabled = false;
            foreach (var r in _probes) SetProbe(r, CBusy, "checking…", CSub);

            var results = await Task.WhenAll(_probes.Select(async r =>
            {
                var res = await Engine.ProbeAsync(r.Url);
                if (!res.Ok && retry) { await Task.Delay(500); res = await Engine.ProbeAsync(r.Url); }
                if (res.Ok) SetProbe(r, CGood, $"reachable · {res.Ms} ms", CGood);
                else SetProbe(r, CBad, $"blocked · {res.Detail}", CBad);
                return res.Ok;
            }));

            TestButton.IsEnabled = !_busy;
            int failed = results.Count(x => !x);
            AppendLog(failed == 0
                ? "Connection check: every Discord service is reachable."
                : $"Connection check: {failed} of {results.Length} Discord services blocked.");
            return failed == 0;
        }

        static void SetProbe(ProbeRow r, Color dot, string text, Color textColor)
        {
            r.Dot.Fill = new SolidColorBrush(dot);
            r.Status.Text = text;
            r.Status.Foreground = new SolidColorBrush(textColor);
        }

        /// <summary>
        /// Scores every method with real TLS handshakes (what the ISP's DPI interferes with)
        /// and keeps the one with the highest success rate. Stops early on a perfect score.
        /// </summary>
        async Task FindBestAsync()
        {
            const int Tries = 12;
            int original = _s.Method;
            SetBusy(true, "Finding the best method…");
            AppendLog($"Scoring each method with {Tries} real connections to Discord…");
            Method best = null;
            int bestScore = -1;

            foreach (var m in Methods.All)
            {
                StatusSub.Text = $"Testing method {m.Id} of {Methods.All.Length}: {m.Name}";
                _s.Method = m.Id;
                try { await ApplyAsync(); }
                catch (Exception ex) { AppendLog($"{m.Name}: couldn't start ({ex.Message})"); continue; }
                int score = await Engine.HandshakeScoreAsync(Tries);
                AppendLog($"{m.Name}: {score}/{Tries} connections got through ({score * 100 / Tries}%).");
                if (score > bestScore) { best = m; bestScore = score; }
                if (score == Tries) break;
            }

            if (best == null || bestScore == 0)
            {
                _s.Method = original;
                AppendLog("No method got through. Went back to your previous choice. " +
                          "Try turning off \"Only touch Discord traffic\", or check that no other VPN/DPI tool is running.");
            }
            else
            {
                _s.Method = best.Id;
                AppendLog(bestScore == Tries
                    ? $"Best: \"{best.Name}\", 100% reliable. Saved as your method."
                    : $"Best: \"{best.Name}\" ({bestScore * 100 / Tries}%). No method was perfect on this network. Saved as your method.");
            }

            try { await ApplyAsync(); } catch (Exception ex) { _s.Save(); Fail(ex); return; }
            _s.WasOn = true;
            _s.Save();
            SelectMethodRow(_s.Method);
            SetBusy(false);
            SetState(UiState.On);
            await TestAsync(retry: true);
        }

        void SelectMethodRow(int id)
        {
            bool prev = _loading;
            _loading = true;
            foreach (RadioButton rb in MethodsPanel.Children)
                rb.IsChecked = (int)rb.Tag == Methods.Get(id).Id;
            _loading = prev;
        }

        // ================= state / visuals =================

        void SetBusy(bool busy, string text = null)
        {
            _busy = busy;
            PowerButton.IsEnabled = !busy;
            FindBestButton.IsEnabled = !busy;
            TestButton.IsEnabled = !busy;
            MethodsPanel.IsEnabled = !busy;
            DiscordOnlySwitch.IsEnabled = !busy;
            DnsSwitch.IsEnabled = !busy;
            if (busy) SetState(UiState.Busy, text);
        }

        void SetState(UiState st, string text = null)
        {
            _state = st;
            Color ring, icon, glow;
            double glowOpacity;
            bool spin = false;
            bool startup = StartupSwitch.IsChecked == true;
            string trayStatus;

            switch (st)
            {
                case UiState.On:
                    ring = CGood; icon = CGood; glow = CGood; glowOpacity = 0.45;
                    StatusTitle.Text = "Discord unblocked";
                    var m = Methods.Get(_s.Method);
                    StatusSub.Text = $"{m.Name} method · " +
                                     (_s.DiscordOnly ? "Discord traffic only" : "all sites") +
                                     (startup ? " · starts with Windows" : "");
                    trayStatus = "● Discord unblocked";
                    break;
                case UiState.Busy:
                    ring = CGray; icon = CBusy; glow = CBusy; glowOpacity = 0.25; spin = true;
                    StatusTitle.Text = text ?? "Working…";
                    StatusSub.Text = "This takes a few seconds";
                    trayStatus = text ?? "Working…";
                    break;
                case UiState.Error:
                    ring = CBad; icon = CBad; glow = CBad; glowOpacity = 0.3;
                    StatusTitle.Text = "Couldn't start";
                    StatusSub.Text = text ?? "See Activity for details";
                    trayStatus = "Couldn't start. Open for details";
                    break;
                default:
                    ring = CGray; icon = CSub; glow = Colors.Black; glowOpacity = 0.7;
                    StatusTitle.Text = "Off";
                    StatusSub.Text = "Click the button to unblock Discord";
                    trayStatus = "○ Off";
                    break;
            }

            var dur = TimeSpan.FromMilliseconds(250);
            _ringBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(ring, dur));
            _iconBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(icon, dur));
            var shadow = (DropShadowEffect)PowerGlow.Effect;
            shadow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(glow, dur));
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(glowOpacity, dur));

            SpinRing.Visibility = spin ? Visibility.Visible : Visibility.Collapsed;
            SpinRot.BeginAnimation(RotateTransform.AngleProperty, spin
                ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever }
                : null);

            if (_tray != null)
            {
                _tray.Icon = _trayIcons[st];
                string tip = "DiscordUnblock: " + (st == UiState.On ? "on" : st == UiState.Busy ? "working…" : st == UiState.Error ? "error" : "off");
                _tray.Text = tip;
                _trayStatus.Text = trayStatus;
                _trayToggle.Text = st == UiState.On ? "Turn off" : "Turn on";
                _trayToggle.Enabled = st != UiState.Busy;
            }
        }

        void AppendLog(string s)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogBox.Text.Length > 60000) LogBox.Text = LogBox.Text.Substring(30000);
                LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {s}\n");
                LogBox.ScrollToEnd();
            }));
        }

        // ================= event handlers =================

        async void Power_Click(object sender, RoutedEventArgs e) => await ToggleAsync();

        async void Test_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            await TestAsync();
        }

        async void FindBest_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            await FindBestAsync();
        }

        async void Method_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || _busy) return;
            _s.Method = (int)((RadioButton)sender).Tag;
            _s.Save();
            AppendLog($"Method set to \"{Methods.Get(_s.Method).Name}\".");
            if (_engine.IsRunning) await RestartIfRunningAsync();
            else if (_state == UiState.Off) SetState(UiState.Off);
        }

        async void Option_Click(object sender, RoutedEventArgs e)
        {
            _s.DiscordOnly = DiscordOnlySwitch.IsChecked == true;
            _s.FixDns = DnsSwitch.IsChecked == true;
            _s.Save();
            await RestartIfRunningAsync();
        }

        void Startup_Click(object sender, RoutedEventArgs e)
        {
            bool enable = StartupSwitch.IsChecked == true;
            try
            {
                if (enable)
                {
                    if (!StartupTask.Create(Environment.ProcessPath))
                        throw new Exception("Windows didn't accept the startup task.");
                    AppendLog("Will open in the tray when you sign in, and restore the bypass if it was on.");
                }
                else
                {
                    StartupTask.Delete();
                    AppendLog("Won't start with Windows anymore.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("Error: " + ex.Message);
            }
            StartupSwitch.IsChecked = StartupTask.Exists();
            if (!_busy && _state != UiState.Error) SetState(_state);
        }

        void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>The close button hides the window to the tray. Quitting is done from the tray menu.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_exiting) { base.OnClosing(e); return; }
            e.Cancel = true;
            Hide();
            if (!_s.TrayHintShown)
            {
                _s.TrayHintShown = true;
                _s.Save();
                _tray.ShowBalloonTip(4000, "DiscordUnblock is in the tray",
                    "It keeps working here. Right-click the shield icon to turn it off or quit.", WF.ToolTipIcon.Info);
            }
        }

        public void ShowFromTray()
        {
            if (_exiting) return;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;   // bring to front reliably
        }

        async Task ExitAppAsync()
        {
            if (_exiting) return;
            _exiting = true;
            // WasOn is kept, so the bypass comes back next time the app opens.
            if (_engine.IsRunning) await _engine.KillAllAsync();
            await Engine.FlushDnsAsync();
            _tray.Visible = false;
            _tray.Dispose();
            Application.Current.Shutdown();
        }
    }

    /// <summary>Dark tray menu matching the app.</summary>
    sealed class DarkMenuRenderer : WF.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? SD.Color.FromArgb(0xE9, 0xEB, 0xF1) : SD.Color.FromArgb(0x8B, 0x91, 0xA3);
            base.OnRenderItemText(e);
        }

        sealed class DarkColors : WF.ProfessionalColorTable
        {
            static readonly SD.Color Bg = SD.Color.FromArgb(0x16, 0x19, 0x21);
            static readonly SD.Color Hover = SD.Color.FromArgb(0x26, 0x2B, 0x3A);
            static readonly SD.Color Line = SD.Color.FromArgb(0x2A, 0x2F, 0x3D);
            public override SD.Color ToolStripDropDownBackground => Bg;
            public override SD.Color MenuBorder => Line;
            public override SD.Color MenuItemBorder => Hover;
            public override SD.Color MenuItemSelected => Hover;
            public override SD.Color MenuItemSelectedGradientBegin => Hover;
            public override SD.Color MenuItemSelectedGradientEnd => Hover;
            public override SD.Color ImageMarginGradientBegin => Bg;
            public override SD.Color ImageMarginGradientMiddle => Bg;
            public override SD.Color ImageMarginGradientEnd => Bg;
            public override SD.Color SeparatorDark => Line;
            public override SD.Color SeparatorLight => Bg;
        }
    }
}
