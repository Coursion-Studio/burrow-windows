using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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

namespace Burrow
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
        WF.ToolStripMenuItem _trayStatus, _trayToggle, _trayRestartDiscord;
        readonly Dictionary<UiState, SD.Icon> _trayIcons = new Dictionary<UiState, SD.Icon>();
        bool _busy, _loading = true, _exiting;
        UiState _state = UiState.Off;

        public MainWindow()
        {
            InitializeComponent();
            PowerRing.Stroke = _ringBrush;
            PowerIcon.Foreground = _iconBrush;
            _engine.Log += AppendLog;
            _engine.UnexpectedExit += () => Dispatcher.BeginInvoke(new Action(OnEngineExitedUnexpectedly));

            BuildMethodRows();
            BuildProbeRows();
            SetupTray();
            VersionText.Text = "v" + Updater.CurrentText;
            LangText.Text = Loc.ShortName(Loc.Current);
            SetState(UiState.Off);
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
                    title.Inlines.Add(new System.Windows.Documents.Run("   " + Loc.T("method.recommended"))
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
            foreach (var (key, url) in Engine.Probes)
            {
                var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(CGray), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                var n = new TextBlock { Text = Loc.T(key), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
                var st = new TextBlock { Text = Loc.T("probe.notChecked"), FontSize = 12, Foreground = new SolidColorBrush(CSub), Margin = new Thickness(8, 0, 0, 0) };
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
            _trayStatus = new WF.ToolStripMenuItem(Loc.T("tray.status.off")) { Enabled = false };
            _trayToggle = new WF.ToolStripMenuItem(Loc.T("tray.turnOn"), null, async (_, __) => await ToggleAsync());
            _trayRestartDiscord = new WF.ToolStripMenuItem(Loc.T("tray.restartDiscord"), null, async (_, __) => await RestartDiscordAsync());
            menu.Items.Add(_trayStatus);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(_trayToggle);
            menu.Items.Add(_trayRestartDiscord);
            menu.Items.Add(new WF.ToolStripMenuItem(Loc.T("tray.open"), null, (_, __) => ShowFromTray()) { Font = new SD.Font("Segoe UI", 9.5f, SD.FontStyle.Bold) });
            menu.Items.Add(new WF.ToolStripMenuItem(Loc.T("tray.updates"), null, async (_, __) => { ShowFromTray(); await CheckForUpdatesAsync(manual: true); }));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(new WF.ToolStripMenuItem(Loc.T("tray.quit"), null, async (_, __) => await ExitAppAsync()));
            foreach (WF.ToolStripItem i in menu.Items) { i.ForeColor = SD.Color.FromArgb(0xE9, 0xEB, 0xF1); i.Padding = new WF.Padding(4, 3, 4, 3); }
            menu.Opening += (_, __) => _trayRestartDiscord.Visible = DiscordClient.IsRunning && _state == UiState.On;

            _tray = new WF.NotifyIcon { Icon = _trayIcons[UiState.Off], Text = Loc.T("tray.tip.off"), Visible = true, ContextMenuStrip = menu };
            _tray.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) ShowFromTray(); };
        }

        async Task InitAsync(bool startedInTray)
        {
            await MigrateFromDiscordUnblockAsync();

            StartupSwitch.IsChecked = StartupTask.Exists();
            DiscordOnlySwitch.IsChecked = _s.DiscordOnly;
            SelectMethodRow(_s.Method);
            UpdateDnsText();
            InitUpdates();
            InitHealth();
            _loading = false;

            AppendLog(_engine.IsVerified() ? Loc.T("log.gdpi.ready") : Loc.T("log.gdpi.notInstalled"));
            if (startedInTray) AppendLog(Loc.T("log.trayStart"));

            // Brand-new install only (no settings yet). People updating keep their method.
            bool firstRun = _s.IsNew && !_s.FirstRunDone;
            if (!_s.FirstRunDone) { _s.FirstRunDone = true; _s.Save(); }

            if (_engine.IsRunning)
            {
                AppendLog(Loc.T("log.alreadyRunning"));
                SetState(UiState.On);
                await TestAsync();
            }
            else if (firstRun && !startedInTray)
            {
                AppendLog(Loc.T("log.firstRun"));
                await FindBestAsync();
            }
            else if (_s.WasOn)
            {
                AppendLog(Loc.T("log.restoring"));
                await TurnOnAsync();
            }
            else SetState(UiState.Off);
        }

        /// <summary>Burrow used to be called DiscordUnblock: move its service and startup task over.</summary>
        async Task MigrateFromDiscordUnblockAsync()
        {
            bool moved = false;
            if (_engine.LegacyServiceExists())
            {
                await _engine.RemoveLegacyServiceAsync();
                if (StartupTask.Create(Environment.ProcessPath)) moved = true;
            }
            if (StartupTask.LegacyExists())
            {
                StartupTask.DeleteLegacy();
                if (StartupTask.Exists() || StartupTask.Create(Environment.ProcessPath)) moved = true;
            }
            if (moved) AppendLog(Loc.T("log.migrated"));
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
            UpdateDnsText();
            await Engine.FlushDnsAsync();
            await Task.Delay(800);
            if (!_engine.IsRunning) throw new Exception(Loc.T("err.notRunning"));
        }

        async Task TurnOnAsync()
        {
            bool discordWasOpen = DiscordClient.IsRunning;
            SetBusy(true, Loc.T("busy.starting"));
            try
            {
                await ApplyAsync();
                _s.WasOn = true; _s.Save();
                SetBusy(false);
                SetState(UiState.On);
                AppendLog(Loc.T("log.on"));
                ShowRestartDiscord(discordWasOpen);
                await TestAsync(retry: true);
            }
            catch (Exception ex) { Fail(ex); }
        }

        async Task TurnOffAsync()
        {
            SetBusy(true, Loc.T("busy.stopping"));
            try
            {
                await _engine.StopAllAsync();
                await Engine.FlushDnsAsync();
                _s.WasOn = false; _s.Save();
                AppendLog(Loc.T("log.off"));
                SetBusy(false);
                SetState(UiState.Off);
                ShowRestartDiscord(false);
                foreach (var r in _probes) SetProbe(r, CGray, Loc.T("probe.notChecked"), CSub);
            }
            catch (Exception ex) { Fail(ex); }
        }

        async Task RestartIfRunningAsync()
        {
            if (!_engine.IsRunning) return;
            SetBusy(true, Loc.T("busy.applying"));
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
            AppendLog(Loc.T("log.error", ex.Message));
            SetBusy(false);
            SetState(UiState.Error, ex.Message);
            if (!IsVisible) _tray.ShowBalloonTip(4000, Loc.T("balloon.error.title"), ex.Message, WF.ToolTipIcon.Warning);
        }

        async Task<bool> TestAsync(bool retry = false)
        {
            TestButton.IsEnabled = false;
            foreach (var r in _probes) SetProbe(r, CBusy, Loc.T("probe.checking"), CSub);

            var results = await Task.WhenAll(_probes.Select(async r =>
            {
                var res = await Engine.ProbeAsync(r.Url);
                if (!res.Ok && retry) { await Task.Delay(500); res = await Engine.ProbeAsync(r.Url); }
                if (res.Ok) SetProbe(r, CGood, Loc.T("probe.ok", res.Ms), CGood);
                else SetProbe(r, CBad, Loc.T("probe.blocked", res.Detail), CBad);
                return res.Ok;
            }));

            TestButton.IsEnabled = !_busy;
            int failed = results.Count(x => !x);
            AppendLog(failed == 0 ? Loc.T("log.check.ok") : Loc.T("log.check.bad", failed, results.Length));
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
        /// Returns the winning method, or null if none got through.
        /// </summary>
        async Task<Method> FindBestAsync(bool quiet = false)
        {
            const int Tries = 12;
            int original = _s.Method;
            bool discordWasOpen = DiscordClient.IsRunning;
            SetBusy(true, Loc.T("busy.findbest"));
            AppendLog(Loc.T("log.findbest.start", Tries));
            Method best = null;
            int bestScore = -1;

            foreach (var m in Methods.All)
            {
                StatusSub.Text = Loc.T("findbest.testing", m.Id, Methods.All.Length, m.Name);
                _s.Method = m.Id;
                try { await ApplyAsync(); }
                catch (Exception ex) { AppendLog(Loc.T("log.findbest.cantStart", m.Name, ex.Message)); continue; }
                int score = await Engine.HandshakeScoreAsync(Tries);
                AppendLog(Loc.T("log.findbest.score", m.Name, score, Tries, score * 100 / Tries));
                if (score > bestScore) { best = m; bestScore = score; }
                if (score == Tries) break;
            }

            Method result = null;
            if (best == null || bestScore == 0)
            {
                _s.Method = original;
                AppendLog(Loc.T("log.findbest.none"));
            }
            else
            {
                _s.Method = best.Id;
                result = best;
                AppendLog(bestScore == Tries
                    ? Loc.T("log.findbest.perfect", best.Name)
                    : Loc.T("log.findbest.partial", best.Name, bestScore * 100 / Tries));
            }

            try { await ApplyAsync(); } catch (Exception ex) { _s.Save(); Fail(ex); return null; }
            _s.WasOn = true;
            _s.Save();
            SelectMethodRow(_s.Method);
            SetBusy(false);
            SetState(UiState.On);
            if (!quiet) ShowRestartDiscord(discordWasOpen);
            await TestAsync(retry: true);
            return result;
        }

        void SelectMethodRow(int id)
        {
            bool prev = _loading;
            _loading = true;
            foreach (RadioButton rb in MethodsPanel.Children)
                rb.IsChecked = (int)rb.Tag == Methods.Get(id).Id;
            _loading = prev;
        }

        // ================= Restart Discord =================

        void ShowRestartDiscord(bool show)
        {
            RestartDiscordPanel.Visibility = show && DiscordClient.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            if (RestartDiscordPanel.Visibility == Visibility.Visible)
            {
                AppendLog(Loc.T("log.restartHint"));
                RestartDiscordPanel.Opacity = 0;
                RestartDiscordPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
            }
        }

        async Task RestartDiscordAsync()
        {
            RestartDiscordButton.IsEnabled = false;
            try
            {
                bool reopened = await DiscordClient.RestartAsync();
                AppendLog(reopened ? Loc.T("log.discord.restarted") : Loc.T("log.discord.restartFailed"));
                RestartDiscordPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) { AppendLog(Loc.T("log.error", ex.Message)); }
            finally { RestartDiscordButton.IsEnabled = true; }
        }

        async void RestartDiscord_Click(object sender, RoutedEventArgs e) => await RestartDiscordAsync();

        // ================= DNS and language pickers =================

        void UpdateDnsText() => DnsText.Text = DnsChoice.Label(_s.Dns, _s.Dns == "auto" && _engine.IsRunning ? _engine.ActiveDns : null);

        /// <summary>A selectable row like the method rows: title, description, radio dot.</summary>
        RadioButton PickerRow(string group, string title, string desc, bool isChecked, Action onPick)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text") });
            if (!string.IsNullOrEmpty(desc))
                sp.Children.Add(new TextBlock { Text = desc, FontSize = 11.5, Foreground = (Brush)FindResource("SubText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
            var rb = new RadioButton { Content = sp, GroupName = group, IsChecked = isChecked, Style = (Style)FindResource("MethodRow") };
            rb.Click += (_, __) => onPick();
            return rb;
        }

        void Dns_Click(object sender, RoutedEventArgs e)
        {
            DnsList.Children.Clear();
            void Add(string id, string title, string desc) => DnsList.Children.Add(PickerRow("dns", title, desc, _s.Dns == id, async () =>
            {
                DnsPopup.IsOpen = false;
                if (_s.Dns == id) return;
                _s.Dns = id;
                _s.Save();
                AppendLog(Loc.T("log.dns.set", DnsChoice.Label(id)));
                UpdateDnsText();
                await RestartIfRunningAsync();
            }));
            Add("auto", Loc.T("dns.auto"), Loc.T("dns.desc.auto"));
            foreach (var d in DnsChoice.All) Add(d.Id, d.Name, Loc.T("dns.desc." + d.Id));
            Add("off", Loc.T("dns.off"), Loc.T("dns.desc.off"));
            DnsPopup.IsOpen = true;
        }

        void Lang_Click(object sender, RoutedEventArgs e)
        {
            LangList.Children.Clear();
            void Add(string code, string title) => LangList.Children.Add(PickerRow("lang", title, null, _s.Language == code, () =>
            {
                LangPopup.IsOpen = false;
                if (_s.Language == code) return;
                _s.Language = code;
                _s.Save();
                if (Loc.Resolve(code) != Loc.Current) RestartApp();   // the whole window is rebuilt in the new language
            }));
            Add("auto", Loc.T("lang.auto", Loc.NativeName(Loc.Resolve("auto"))));
            foreach (var code in Loc.Languages) Add(code, Loc.NativeName(code));
            LangPopup.IsOpen = true;
        }

        /// <summary>Starts a fresh copy (it waits for this one to exit) and quits. Used to switch language.</summary>
        async void RestartApp()
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--after " + Environment.ProcessId) { UseShellExecute = false });
            await ExitAppAsync();
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
            DnsButton.IsEnabled = !busy;
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
                    StatusTitle.Text = Loc.T("status.on.title");
                    var m = Methods.Get(_s.Method);
                    StatusSub.Text = Loc.T("status.on.sub", m.Name,
                        _s.DiscordOnly ? Loc.T("status.scope.discord") : Loc.T("status.scope.all")) +
                        (startup ? Loc.T("status.startup") : "");
                    trayStatus = Loc.T("tray.status.on");
                    break;
                case UiState.Busy:
                    ring = CGray; icon = CBusy; glow = CBusy; glowOpacity = 0.25; spin = true;
                    StatusTitle.Text = text ?? Loc.T("status.working");
                    StatusSub.Text = Loc.T("status.busy.sub");
                    trayStatus = text ?? Loc.T("status.working");
                    break;
                case UiState.Error:
                    ring = CBad; icon = CBad; glow = CBad; glowOpacity = 0.3;
                    StatusTitle.Text = Loc.T("status.error.title");
                    StatusSub.Text = text ?? Loc.T("status.error.sub");
                    trayStatus = Loc.T("tray.status.error");
                    break;
                default:
                    ring = CGray; icon = CSub; glow = Colors.Black; glowOpacity = 0.7;
                    StatusTitle.Text = Loc.T("status.off.title");
                    StatusSub.Text = Loc.T("status.off.sub");
                    trayStatus = Loc.T("tray.status.off");
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
                string tip = st == UiState.On ? "tray.tip.on" : st == UiState.Busy ? "tray.tip.busy" : st == UiState.Error ? "tray.tip.error" : "tray.tip.off";
                _tray.Text = Loc.T(tip);
                _trayStatus.Text = trayStatus;
                _trayToggle.Text = st == UiState.On ? Loc.T("tray.turnOff") : Loc.T("tray.turnOn");
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
            AppendLog(Loc.T("log.method", Methods.Get(_s.Method).Name));
            if (_engine.IsRunning) await RestartIfRunningAsync();
            else if (_state == UiState.Off) SetState(UiState.Off);
        }

        async void Option_Click(object sender, RoutedEventArgs e)
        {
            _s.DiscordOnly = DiscordOnlySwitch.IsChecked == true;
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
                    if (!StartupTask.Create(Environment.ProcessPath)) throw new Exception(Loc.T("err.startupTask"));
                    AppendLog(Loc.T("log.startup.on"));
                }
                else
                {
                    StartupTask.Delete();
                    AppendLog(Loc.T("log.startup.off"));
                }
            }
            catch (Exception ex) { AppendLog(Loc.T("log.error", ex.Message)); }
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
                _tray.ShowBalloonTip(4000, Loc.T("balloon.tray.title"), Loc.T("balloon.tray.text"), WF.ToolTipIcon.Info);
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
