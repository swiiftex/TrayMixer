using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayMixer
{
    sealed class TrayApp : ApplicationContext
    {
        const int ReopenGuardMilliseconds = 400;
        const int MaxTooltipLength = 63;

        readonly Settings _settings = Settings.Load();
        readonly ContextMenuStrip _menu;
        readonly ToolStripMenuItem _startupItem;
        readonly MixerPopup _popup;
        readonly NotifyIcon _tray;
        readonly AudioEngine _engine;
        readonly System.Windows.Forms.Timer _saveTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        readonly EventWaitHandle _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
        readonly EventWaitHandle _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        readonly RegisteredWaitHandle _quitWait, _showWait;
        MixerSnapshot _snapshot;
        bool _openOnFirstSnapshot;
        bool _lightTaskbar = SystemTheme.TaskbarUsesLightTheme;
        string _iconKey;

        public TrayApp(bool openMixer)
        {
            var openItem = new ToolStripMenuItem("Open mixer", null, (s, e) => ShowPopup(force: true, Cursor.Position));
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            _startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => SetStartup(!Startup.IsEnabled));
            _menu = new ContextMenuStrip();
            _menu.Items.AddRange(new ToolStripItem[]
            {
                openItem,
                new ToolStripMenuItem("Sound settings", null, (s, e) => OpenSoundSettings()),
                _startupItem,
                new ToolStripSeparator(),
                new ToolStripMenuItem("Exit", null, (s, e) => ExitThread()),
            });
            _menu.Opening += (s, e) => _startupItem.Checked = Startup.IsEnabled;

            // Creating the first controls installs the WinForms SynchronizationContext the audio thread posts back to.
            _popup = new MixerPopup(_menu);
            _popup.LevelChanged += OnLevelChanged;
            _popup.VisibleChanged += (s, e) =>
            {
                _engine.Live = _popup.Visible;
                if (!_popup.Visible)
                    SaveNow();
            };
            _saveTimer.Tick += (s, e) => SaveNow();

            _tray = new NotifyIcon { ContextMenuStrip = _menu };
            _tray.MouseClick += OnTrayClick;
            _tray.BalloonTipClicked += (s, e) => ShowPopup(force: true, TrayCorner());
            UpdateTray();
            _tray.Visible = true;

            _engine = new AudioEngine(_settings.Volumes, OnSnapshot, OnRememberedChanged);
            _engine.Start();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            // Another launch of the app (Start menu, installer) asks this copy to open its mixer or to quit.
            var ui = SynchronizationContext.Current;
            _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (state, timedOut) => ui.Post(_ => ExitThread(), null), null, -1, false);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (state, timedOut) => ui.Post(_ => ShowPopup(force: true, TrayCorner()), null), null, -1, false);

            _openOnFirstSnapshot = openMixer;
            if (_settings.IsFirstRun)
            {
                _settings.Save();
                if (!openMixer)
                    _tray.ShowBalloonTip(5000, "Tray Mixer", "Click the mixer icon in the tray to set each app's volume.", ToolTipIcon.None);
            }
            try { Startup.RepairPath(); }
            catch (UnauthorizedAccessException) { }
        }

        void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                ShowPopup(force: false, Cursor.Position);
            else if (e.Button == MouseButtons.Middle && _snapshot != null && _snapshot.HasDevice)
                _engine.SetMaster(new Level(_snapshot.Master.Volume, !_snapshot.Master.Muted));
        }

        void ShowPopup(bool force, Point anchor)
        {
            // Clicking the tray icon while the flyout is open first deactivates (hides) it; don't reopen it straight away.
            if (!force && unchecked(Environment.TickCount - _popup.HiddenAtTick) < ReopenGuardMilliseconds)
                return;
            if (_snapshot != null)
                _popup.ApplySnapshot(_snapshot);
            _popup.ShowNear(anchor);
        }

        // Where the notification area usually is, for opening the mixer when it wasn't opened from the tray icon.
        static Point TrayCorner()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            return new Point(work.Right - 1, work.Bottom - 1);
        }

        void OnLevelChanged(string key, Level level)
        {
            if (key == MixerPopup.MasterKey)
                _engine.SetMaster(level);
            else
                _engine.SetApp(key, level);
        }

        void OnSnapshot(MixerSnapshot snapshot)
        {
            _snapshot = snapshot;
            if (_popup.Visible)
                _popup.ApplySnapshot(snapshot);
            UpdateTray();
            if (_openOnFirstSnapshot)
            {
                _openOnFirstSnapshot = false;
                ShowPopup(force: true, TrayCorner());
            }
        }

        void OnRememberedChanged(Dictionary<string, Level> volumes)
        {
            _settings.Volumes = volumes;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void SaveNow()
        {
            _saveTimer.Stop();
            _settings.Save();
        }

        void SetStartup(bool enabled)
        {
            try
            {
                Startup.SetEnabled(enabled);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                MessageBox.Show("Couldn't change the startup setting:\n" + ex.Message, "Tray Mixer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static void OpenSoundSettings()
        {
            try { Process.Start("ms-settings:sound"); }
            catch (Win32Exception) { }
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
            {
                _lightTaskbar = SystemTheme.TaskbarUsesLightTheme;
                UpdateTray();
            }
        }

        void UpdateTray()
        {
            bool hasDevice = _snapshot != null && _snapshot.HasDevice;
            string glyph = hasDevice && _snapshot.Master.Muted ? Glyphs.Mute : Glyphs.Mixer;
            string key = glyph + _lightTaskbar;
            if (key != _iconKey)
            {
                Icon old = _tray.Icon;
                _tray.Icon = TrayIcons.Create(glyph, _lightTaskbar);
                old?.Dispose();
                _iconKey = key;
            }

            string text;
            if (_snapshot == null)
                text = "Tray Mixer";
            else if (!hasDevice)
                text = "No output device";
            else
            {
                string level = ": " + (_snapshot.Master.Muted ? "muted" : _snapshot.Master.Volume + "%");
                string name = _snapshot.DeviceName;
                if (name.Length + level.Length > MaxTooltipLength)
                    name = name.Substring(0, MaxTooltipLength - level.Length - 1) + "…";
                text = name + level;
            }
            if (_tray.Text != text)
                _tray.Text = text;
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _quitWait.Unregister(null);
            _showWait.Unregister(null);
            _quitSignal.Dispose();
            _showSignal.Dispose();
            SaveNow();
            _engine.Dispose();
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _popup.Dispose();
            _menu.Dispose();
            _saveTimer.Dispose();
            base.ExitThreadCore();
        }
    }
}
