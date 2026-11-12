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
        readonly ToolStripMenuItem _openItem, _devicesItem, _startupItem;
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
            _openItem = new ToolStripMenuItem("Open mixer", null, (s, e) => ShowPopup(force: true, Cursor.Position));
            _openItem.Font = new Font(_openItem.Font, FontStyle.Bold);
            _devicesItem = new ToolStripMenuItem("Volume controls");
            _startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => SetStartup(!Startup.IsEnabled));
            _menu = new ContextMenuStrip { ImageScalingSize = SystemInformation.SmallIconSize };
            _menu.Items.AddRange(new ToolStripItem[]
            {
                _openItem,
                _devicesItem,
                new ToolStripSeparator(),
                new ToolStripMenuItem("Sound settings", null, (s, e) => Launch("ms-settings:sound", null)),
                new ToolStripMenuItem("Sound settings (legacy)", null, (s, e) => Launch("control.exe", "mmsys.cpl")),
                new ToolStripSeparator(),
                _startupItem,
                new ToolStripMenuItem("Exit", null, (s, e) => ExitThread()),
            });
            _menu.Opening += (s, e) =>
            {
                _openItem.Visible = !_popup.MenuOpenedHere;
                _startupItem.Checked = Startup.IsEnabled;
                BuildDevicesMenu();
            };

            // Creating the first controls installs the WinForms SynchronizationContext the audio thread posts back to.
            _popup = new MixerPopup(_menu) { DeviceChoices = _settings.Devices };
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
            else if (e.Button == MouseButtons.Middle && _snapshot?.DefaultDevice is DeviceState device)
                _engine.SetDevice(device.Id, new Level(device.Level.Volume, !device.Level.Muted));
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
            if (key.StartsWith(MixerPopup.DevicePrefix, StringComparison.Ordinal))
                _engine.SetDevice(key.Substring(MixerPopup.DevicePrefix.Length), level);
            else
                _engine.SetApp(key, level);
        }

        // ---- Volume controls menu ----

        static readonly DeviceIcon[] IconChoices = { DeviceIcon.Speaker, DeviceIcon.Media, DeviceIcon.Music, DeviceIcon.Chat, DeviceIcon.Game };
        readonly Dictionary<DeviceIcon, Image> _iconImages = new Dictionary<DeviceIcon, Image>();

        // With nothing chosen, the mixer shows the default device, so the menu shows that one as ticked.
        bool IsShown(string deviceId) => _settings.Devices.Exists(c => c.Shown)
            ? _settings.ChoiceFor(deviceId)?.Shown == true
            : deviceId == _snapshot?.DefaultDevice?.Id;

        void BuildDevicesMenu()
        {
            foreach (ToolStripItem item in _devicesItem.DropDownItems)
                item.Dispose();
            _devicesItem.DropDownItems.Clear();
            if (_snapshot == null || _snapshot.Devices.Count == 0)
            {
                _devicesItem.DropDownItems.Add(new ToolStripMenuItem("No output devices") { Enabled = false });
                return;
            }
            foreach (DeviceState device in _snapshot.Devices)
            {
                string id = device.Id;
                DeviceIcon current = _settings.ChoiceFor(id)?.Icon ?? DeviceIcon.Speaker;
                var item = new ToolStripMenuItem(device.Name + (device.IsDefault ? " (default)" : ""), IconImage(current))
                {
                    Checked = IsShown(id),
                };
                item.DropDownItems.Add(new ToolStripMenuItem("Show in mixer", null, (s, e) => ToggleShown(id)) { Checked = IsShown(id) });
                item.DropDownItems.Add(new ToolStripSeparator());
                foreach (DeviceIcon icon in IconChoices)
                {
                    DeviceIcon choice = icon;
                    item.DropDownItems.Add(new ToolStripMenuItem(icon.ToString(), IconImage(icon), (s, e) => SetIcon(id, choice)) { Checked = icon == current });
                }
                _devicesItem.DropDownItems.Add(item);
            }
        }

        Image IconImage(DeviceIcon icon)
        {
            if (!_iconImages.TryGetValue(icon, out Image image))
            {
                image = TrayIcons.GlyphBitmap(Glyphs.ForDevice(icon, Level.Default), SystemInformation.SmallIconSize.Height, SystemColors.MenuText);
                _iconImages[icon] = image;
            }
            return image;
        }

        DeviceChoice ChoiceFor(string deviceId)
        {
            DeviceChoice choice = _settings.ChoiceFor(deviceId);
            if (choice == null)
            {
                choice = new DeviceChoice { Id = deviceId };
                _settings.Devices.Add(choice);
            }
            return choice;
        }

        void ToggleShown(string deviceId)
        {
            bool nothingChosen = !_settings.Devices.Exists(c => c.Shown);
            string defaultId = _snapshot?.DefaultDevice?.Id;
            if (nothingChosen && defaultId != null && deviceId != defaultId)
            {
                // Adding a second control keeps the default device that was shown so far.
                ShowLast(ChoiceFor(defaultId));
                ShowLast(ChoiceFor(deviceId));
            }
            else if (IsShown(deviceId))
                ChoiceFor(deviceId).Shown = false;
            else
                ShowLast(ChoiceFor(deviceId));
            DeviceChoicesChanged();
        }

        // Newly shown devices go after the ones already shown.
        void ShowLast(DeviceChoice choice)
        {
            _settings.Devices.Remove(choice);
            _settings.Devices.Add(choice);
            choice.Shown = true;
        }

        void SetIcon(string deviceId, DeviceIcon icon)
        {
            ChoiceFor(deviceId).Icon = icon;
            DeviceChoicesChanged();
        }

        void DeviceChoicesChanged()
        {
            _settings.Devices.RemoveAll(c => !c.Shown && c.Icon == DeviceIcon.Speaker);
            SaveNow();
            _popup.RefreshLayout();
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

        static void Launch(string target, string arguments)
        {
            try { Process.Start(target, arguments); }
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
            DeviceState device = _snapshot?.DefaultDevice;
            string glyph = device != null && device.Level.Muted ? Glyphs.Mute : Glyphs.Mixer;
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
            else if (device == null)
                text = "No output device";
            else
            {
                string level = ": " + (device.Level.Muted ? "muted" : device.Level.Volume + "%");
                string name = device.Name;
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
            foreach (Image image in _iconImages.Values)
                image.Dispose();
            _saveTimer.Dispose();
            base.ExitThreadCore();
        }
    }
}
