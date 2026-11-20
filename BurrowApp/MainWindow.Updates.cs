using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace Burrow
{
    /// <summary>Over-the-air updates: the daily check, the update card, and installing.</summary>
    public partial class MainWindow
    {
        DispatcherTimer _updTimer;
        UpdateInfo _pendingUpdate;
        string _shownThisRun;
        bool _checking, _installing;

        static readonly Brush StatusBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9CCD8"));

        string FeedUrl => string.IsNullOrWhiteSpace(_s.UpdateFeed) ? Updater.DefaultFeed : _s.UpdateFeed;

        /// <summary>The app icon drawn at full resolution (an .ico frame scaled up looks soft).</summary>
        static System.Windows.Media.Imaging.BitmapImage CrispAppIcon(int px)
        {
            using var bmp = IconArt.RenderApp(px);
            var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.StreamSource = ms;
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }

        /// <summary>Checks 20 s after launch, then every hour; it only asks the feed once a day.</summary>
        void InitUpdates()
        {
            AutoUpdateSwitch.IsChecked = _s.AutoUpdate;
            _updTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(20) };
            _updTimer.Tick += async (_, __) =>
            {
                _updTimer.Interval = TimeSpan.FromHours(1);
                if (!_s.AutoUpdate || _exiting) return;
                if (DateTime.UtcNow - new DateTime(_s.LastUpdateCheck, DateTimeKind.Utc) < TimeSpan.FromHours(24)) return;
                await CheckForUpdatesAsync(manual: false);
            };
            _updTimer.Start();
            _tray.BalloonTipClicked += (_, __) => { if (_pendingUpdate != null) ShowFromTray(); };
        }

        /// <summary>manual = the user asked, so always report the result. Automatic checks stay quiet unless there's an update.</summary>
        async Task CheckForUpdatesAsync(bool manual)
        {
            if (_checking || _installing) return;
            _checking = true;
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesButton.Content = Loc.T("upd.checking");
            try
            {
                var u = await Updater.CheckAsync(FeedUrl);
                _s.LastUpdateCheck = DateTime.UtcNow.Ticks;
                _s.Save();
                if (u == null)
                {
                    AppendLog(Loc.T("log.upd.none", Updater.CurrentText));
                    if (manual) ShowUpdateMessage(Loc.T("upd.uptodate.title"), Loc.T("upd.uptodate.text", Updater.CurrentText));
                    return;
                }
                string v = Updater.Text(u.Version);
                AppendLog(Loc.T("log.upd.available", v));
                if (!manual && (v == _s.SkippedVersion || v == _shownThisRun)) return;
                _shownThisRun = v;
                ShowUpdate(u);
                if (!IsVisible)
                    _tray.ShowBalloonTip(6000, Loc.T("upd.title", v), Loc.T("balloon.upd.text"), WF.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                AppendLog(Loc.T("log.upd.failed", Updater.Friendly(ex)));
                if (manual) ShowUpdateMessage(Loc.T("upd.failed.title"), Updater.Friendly(ex));
            }
            finally
            {
                _checking = false;
                CheckUpdatesButton.IsEnabled = true;
                CheckUpdatesButton.Content = Loc.T("xaml.checkNow");
            }
        }

        // ---------------- the card ----------------

        void ShowUpdate(UpdateInfo u)
        {
            _pendingUpdate = u;
            UpdTitle.Text = Loc.T("upd.title", Updater.Text(u.Version));
            UpdSub.Text = Loc.T("upd.sub", Updater.CurrentText) + (string.IsNullOrEmpty(u.Published) ? "" : Loc.T("upd.released", u.Published));
            UpdNotes.Children.Clear();
            foreach (var n in u.Notes)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = "•", Foreground = (Brush)FindResource("Accent"), FontSize = 12.5 });
                var t = new TextBlock { Text = n, FontSize = 12.5, Foreground = StatusBrush, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
                Grid.SetColumn(t, 1);
                g.Children.Add(t);
                UpdNotes.Children.Add(g);
            }
            UpdNotesCard.Visibility = u.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ResetProgress();
            UpdSkip.Visibility = Visibility.Visible;
            UpdInstall.Visibility = Visibility.Visible;
            UpdLater.Content = Loc.T("upd.later");
            OpenUpdateCard();
        }

        void ShowUpdateMessage(string title, string text)
        {
            _pendingUpdate = null;
            UpdTitle.Text = title;
            UpdSub.Text = text;
            UpdNotesCard.Visibility = Visibility.Collapsed;
            ResetProgress();
            UpdSkip.Visibility = Visibility.Collapsed;
            UpdInstall.Visibility = Visibility.Collapsed;
            UpdLater.Content = Loc.T("upd.ok");
            OpenUpdateCard();
        }

        void ResetProgress()
        {
            UpdStatus.Visibility = Visibility.Collapsed;
            UpdStatus.Foreground = StatusBrush;
            UpdTrack.Visibility = Visibility.Collapsed;
            UpdFill.BeginAnimation(WidthProperty, null);
            UpdFill.Width = 0;
            UpdButtons.IsEnabled = true;
        }

        void OpenUpdateCard()
        {
            if (UpdIcon.Source == null) UpdIcon.Source = CrispAppIcon(180);
            UpdateOverlay.Visibility = Visibility.Visible;
            UpdateOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            UpdateCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = spring });
            UpdateCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = spring });
        }

        void CloseUpdateCard()
        {
            if (_installing) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, __) => { if (UpdateOverlay.Opacity == 0) UpdateOverlay.Visibility = Visibility.Collapsed; };
            UpdateOverlay.BeginAnimation(OpacityProperty, fade);
        }

        void SetUpdateBar(double fraction)
        {
            double w = Math.Max(0, UpdTrack.ActualWidth * Math.Min(1, fraction));
            UpdFill.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        }

        // ---------------- handlers ----------------

        async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

        void AutoUpdate_Click(object sender, RoutedEventArgs e)
        {
            _s.AutoUpdate = AutoUpdateSwitch.IsChecked == true;
            _s.Save();
            AppendLog(_s.AutoUpdate ? Loc.T("log.autoUpdate.on") : Loc.T("log.autoUpdate.off"));
        }

        void UpdLater_Click(object sender, RoutedEventArgs e) => CloseUpdateCard();

        void UpdateScrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseUpdateCard();

        void UpdSkip_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate != null)
            {
                _s.SkippedVersion = Updater.Text(_pendingUpdate.Version);
                _s.Save();
                AppendLog(Loc.T("log.upd.skip", _s.SkippedVersion));
            }
            CloseUpdateCard();
        }

        async void UpdInstall_Click(object sender, RoutedEventArgs e)
        {
            var u = _pendingUpdate;
            if (u == null || _installing) return;
            _installing = true;
            UpdButtons.IsEnabled = false;
            UpdStatus.Visibility = Visibility.Visible;
            UpdTrack.Visibility = Visibility.Visible;
            UpdStatus.Text = Loc.T("upd.downloading");
            try
            {
                var progress = new Progress<int>(p => { UpdStatus.Text = Loc.T("upd.downloadingPct", p); SetUpdateBar(p / 100.0 * 0.85); });
                string path = await Updater.DownloadAsync(u, progress);
                UpdStatus.Text = Loc.T("upd.verified");
                SetUpdateBar(1);
                AppendLog(Loc.T("log.upd.installing", Updater.Text(u.Version)));
                await Task.Delay(700);
                Updater.StartInstaller(path);
                await ExitAppAsync();
            }
            catch (Exception ex)
            {
                _installing = false;
                UpdStatus.Text = Updater.Friendly(ex);
                UpdStatus.Foreground = new SolidColorBrush(CBad);
                UpdFill.BeginAnimation(WidthProperty, null);
                UpdFill.Width = 0;
                UpdButtons.IsEnabled = true;
                AppendLog(Loc.T("log.upd.error", Updater.Friendly(ex)));
            }
        }
    }
}
