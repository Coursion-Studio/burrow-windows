using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace Burrow
{
    /// <summary>
    /// Self-healing. While the bypass is on, Burrow quietly checks every few minutes that Discord still gets through.
    /// - GoodbyeDPI stopped on its own (crash, antivirus) → start it again (at most 3 times in 10 minutes).
    /// - Discord blocked twice in a row while the internet itself works → find a method that works now, and say so.
    /// </summary>
    public partial class MainWindow
    {
        static readonly TimeSpan HealthInterval = TimeSpan.FromMinutes(5);
        const int HealthTries = 6, HealthMinOk = 4;

        DispatcherTimer _healthTimer;
        int _strikes;
        bool _healing;
        readonly List<DateTime> _restarts = new List<DateTime>();

        void InitHealth()
        {
            _healthTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = HealthInterval };
            _healthTimer.Tick += async (_, __) => await HealthCheckAsync();
            _healthTimer.Start();
        }

        async Task HealthCheckAsync()
        {
            if (_busy || _healing || _exiting || _state != UiState.On) return;
            _healing = true;
            try
            {
                if (!_engine.IsRunning) { await RestartEngineAsync(); return; }

                int ok = await Engine.HandshakeScoreAsync(HealthTries);
                if (ok >= HealthMinOk) { _strikes = 0; return; }

                if (!await Engine.InternetUpAsync()) { AppendLog(Loc.T("log.heal.offline")); return; }

                _strikes++;
                AppendLog(Loc.T("log.heal.blocked", ok, HealthTries));
                if (_strikes < 2) return;   // one bad sample can be a hiccup; act on the second

                _strikes = 0;
                AppendLog(Loc.T("log.heal.refind"));
                var found = await FindBestAsync(quiet: true);
                if (found != null)
                    _tray.ShowBalloonTip(6000, Loc.T("balloon.heal.title"), Loc.T("balloon.heal.fixed", found.Name), WF.ToolTipIcon.Info);
                else
                    _tray.ShowBalloonTip(6000, Loc.T("balloon.heal.title"), Loc.T("balloon.heal.failed"), WF.ToolTipIcon.Warning);
            }
            catch (Exception ex) { AppendLog(Loc.T("log.error", ex.Message)); }
            finally { _healing = false; }
        }

        /// <summary>GoodbyeDPI exited without being asked to. Bring it back unless it keeps dying.</summary>
        async void OnEngineExitedUnexpectedly()
        {
            if (_busy || _exiting || _state != UiState.On) return;
            await Task.Delay(3000);
            if (_busy || _exiting || _engine.IsRunning) return;
            await RestartEngineAsync();
        }

        async Task RestartEngineAsync()
        {
            _restarts.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(10));
            if (_restarts.Count >= 3)
            {
                AppendLog(Loc.T("log.heal.giveUp"));
                _s.WasOn = false; _s.Save();
                SetState(UiState.Error, Loc.T("log.heal.giveUp"));
                _tray.ShowBalloonTip(6000, Loc.T("balloon.error.title"), Loc.T("log.heal.giveUp"), WF.ToolTipIcon.Warning);
                return;
            }
            _restarts.Add(DateTime.UtcNow);
            AppendLog(Loc.T("log.heal.restart"));
            SetBusy(true, Loc.T("busy.starting"));
            try
            {
                await ApplyAsync();
                SetBusy(false);
                SetState(UiState.On);
            }
            catch (Exception ex) { Fail(ex); }
        }
    }
}
