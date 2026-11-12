using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TrayMixer
{
    // Borderless flyout shown above the tray icon, painted by hand so it scales cleanly on any monitor.
    // One output device gets a horizontal volume row; two or more are laid out side by side as vertical sliders.
    // Below them is one row per app, at most six at a time, with the rest scrolled.
    sealed class MixerPopup : Form
    {
        public const string DevicePrefix = "#device:";

        const int BaseWidth = 360;
        const int HeaderHeight = 48;
        const int DeviceRowHeight = 44;
        const int ColumnWidth = 84;
        const int ColumnMaxWidth = 96;
        const int ColumnHeight = 232;
        const int ColumnTrackTop = 52;
        const int ColumnTrackLength = 112;
        const int SeparatorHeight = 17;
        const int AppRowHeight = 52;
        const int MaxVisibleApps = 6;
        const int EmptyHeight = 40;
        const int BottomPadding = 8;
        const int PendingMilliseconds = 400;
        const float PeakFalloff = 0.82f;

        enum Part { None, Icon, Slider }

        sealed class Row
        {
            public string Key;
            public bool IsDevice;
            public bool Vertical;
            public RowKind Kind;
            public DeviceIcon Icon;
            public string Label;
            public string ImagePath;
            public Level Level;
            public float Peak;
            public Rectangle Box;   // devices: client coordinates; apps: content coordinates before scrolling
        }

        readonly ContextMenuStrip _menu;
        readonly List<Row> _rows = new List<Row>();
        readonly Dictionary<string, int> _pendingUntil = new Dictionary<string, int>();
        MixerSnapshot _snapshot;
        Palette _palette = Palette.Current();
        float _scale = 1f;
        Font _titleFont, _labelFont, _smallFont, _valueFont, _iconFont, _badgeFont;
        bool _dwmDrawsBorder;
        Point _anchor;
        string _layoutSignature;
        int _width = BaseWidth;
        int _appsTop, _appsBottom, _scroll, _maxScroll;
        string _dragKey, _hoverKey, _pressKey, _focusKey;
        Part _hoverPart;
        bool _hoverMore, _pressMore, _showFocus;
        bool _draggingScroll, _hoverScroll;
        float _scrollGrab;
        int _wheelRemainder;

        public event Action<string, Level> LevelChanged;

        public int HiddenAtTick { get; private set; } = Environment.TickCount - 10000;

        // True while the shared menu is open from this flyout's ⋯ button rather than from the tray icon.
        public bool MenuOpenedHere { get; private set; }

        // Which devices get a volume control and with which icon; owned by the settings.
        public List<DeviceChoice> DeviceChoices { get; set; } = new List<DeviceChoice>();

        public MixerPopup(ContextMenuStrip menu)
        {
            _menu = menu;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Tray Mixer";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            RebuildFonts();

            _menu.Closed += (s, e) =>
            {
                if (!MenuOpenedHere)
                    return;
                MenuOpenedHere = false;
                if (Visible && GetForegroundWindow() != Handle)
                    HidePopup();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow;
                cp.ClassStyle |= CsDropShadow;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = DwmCornerRound;
            _dwmDrawsBorder = DwmSetWindowAttribute(Handle, DwmaWindowCornerPreference, ref round, sizeof(int)) == 0;
        }

        // ---- Data ----

        public void ApplySnapshot(MixerSnapshot snapshot)
        {
            _snapshot = snapshot;
            var previous = new Dictionary<string, Row>();
            foreach (var row in _rows)
                previous[row.Key] = row;

            _rows.Clear();
            List<DeviceState> devices = ShownDevices(snapshot);
            foreach (var device in devices)
            {
                _rows.Add(new Row
                {
                    Key = DevicePrefix + device.Id,
                    IsDevice = true,
                    Vertical = devices.Count > 1,
                    Icon = DeviceChoices.Find(c => c.Id == device.Id)?.Icon ?? DeviceIcon.Speaker,
                    Label = device.Name,
                    Level = device.Level,
                    Peak = device.Peak,
                });
            }
            foreach (var app in snapshot.Apps)
                _rows.Add(new Row { Key = app.Key, Kind = app.Kind, Label = app.Name, ImagePath = app.ImagePath, Level = app.Level, Peak = app.Peak });

            int now = Environment.TickCount;
            foreach (var row in _rows)
            {
                if (!previous.TryGetValue(row.Key, out Row old))
                    continue;
                row.Peak = Math.Max(row.Peak, old.Peak * PeakFalloff);
                // Keep what the user just set until the audio thread has caught up with it.
                bool pending = _pendingUntil.TryGetValue(row.Key, out int until) && unchecked(until - now) > 0;
                if (pending || row.Key == _dragKey)
                    row.Level = old.Level;
            }

            Arrange(resizeWindow: Visible);
            Invalidate();
        }

        // Re-applies the last snapshot, e.g. after the device choices changed.
        public void RefreshLayout()
        {
            if (_snapshot != null)
                ApplySnapshot(_snapshot);
        }

        List<DeviceState> ShownDevices(MixerSnapshot snapshot)
        {
            var shown = new List<DeviceState>();
            foreach (var choice in DeviceChoices)
            {
                DeviceState device = choice.Shown ? snapshot.Devices.Find(d => d.Id == choice.Id) : null;
                if (device != null)
                    shown.Add(device);
            }
            if (shown.Count == 0 && snapshot.DefaultDevice != null)
                shown.Add(snapshot.DefaultDevice);
            return shown;
        }

        // ---- Showing and placement ----

        public void ShowNear(Point anchor)
        {
            if (!IsHandleCreated)
                CreateHandle();
            _anchor = anchor;
            _palette = Palette.Current();
            _scale = DpiAt(anchor) / 96f;
            _scroll = 0;
            _showFocus = false;
            _focusKey = null;
            RebuildFonts();
            _layoutSignature = null;
            Arrange(resizeWindow: true);
            if (_dwmDrawsBorder)
            {
                int color = ColorTranslator.ToWin32(_palette.Border);
                DwmSetWindowAttribute(Handle, DwmaBorderColor, ref color, sizeof(int));
            }
            Show();
            Activate();
            SetForegroundWindow(Handle);
        }

        void HidePopup()
        {
            if (!Visible)
                return;
            HiddenAtTick = Environment.TickCount;
            _dragKey = _hoverKey = _pressKey = null;
            _hoverMore = _pressMore = _draggingScroll = _hoverScroll = false;
            Hide();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!MenuOpenedHere)
                HidePopup();
        }

        void Arrange(bool resizeWindow)
        {
            var devices = _rows.FindAll(r => r.IsDevice);
            var apps = _rows.FindAll(r => !r.IsDevice);
            _width = Math.Max(S(BaseWidth), devices.Count > 1 ? devices.Count * S(ColumnWidth) + S(32) : 0);

            int y = S(HeaderHeight);
            if (devices.Count == 1)
            {
                devices[0].Box = new Rectangle(0, y, _width, S(DeviceRowHeight));
                y += S(DeviceRowHeight) + S(SeparatorHeight);
            }
            else if (devices.Count > 1)
            {
                int column = Math.Min(S(ColumnMaxWidth), (_width - S(32)) / devices.Count);
                int left = (_width - column * devices.Count) / 2;
                for (int i = 0; i < devices.Count; i++)
                    devices[i].Box = new Rectangle(left + i * column, y, column, S(ColumnHeight));
                y += S(ColumnHeight) + S(SeparatorHeight);
            }

            _appsTop = y;
            for (int i = 0; i < apps.Count; i++)
                apps[i].Box = new Rectangle(0, y + i * S(AppRowHeight), _width, S(AppRowHeight));
            int viewport = apps.Count == 0 ? S(EmptyHeight) : Math.Min(apps.Count, MaxVisibleApps) * S(AppRowHeight);

            Rectangle work = Screen.FromPoint(_anchor).WorkingArea;
            int height = Math.Min(_appsTop + viewport + S(BottomPadding), work.Height - 2 * S(12));
            _appsBottom = height - S(BottomPadding);
            _maxScroll = Math.Max(0, apps.Count * S(AppRowHeight) - (_appsBottom - _appsTop));
            _scroll = Math.Max(0, Math.Min(_scroll, _maxScroll));

            string signature = _width + "|" + height + "|" + _scale;
            if (resizeWindow && signature != _layoutSignature)
            {
                _layoutSignature = signature;
                Bounds = BoundsNear(_anchor, new Size(_width, height));
            }
        }

        Rectangle BoundsNear(Point anchor, Size size)
        {
            Screen screen = Screen.FromPoint(anchor);
            Rectangle work = screen.WorkingArea, full = screen.Bounds;
            int w = size.Width, h = size.Height, margin = S(12);
            int x = anchor.X - w / 2;
            int y;
            if (work.Bottom < full.Bottom)
                y = work.Bottom - h - margin;
            else if (work.Top > full.Top)
                y = work.Top + margin;
            else if (work.Left > full.Left)
            {
                x = work.Left + margin;
                y = anchor.Y - h / 2;
            }
            else if (work.Right < full.Right)
            {
                x = work.Right - w - margin;
                y = anchor.Y - h / 2;
            }
            else
                y = anchor.Y - h - S(24);

            x = Math.Max(work.Left + margin, Math.Min(x, work.Right - w - margin));
            y = Math.Max(work.Top + margin, Math.Min(y, work.Bottom - h - margin));
            return new Rectangle(x, y, w, h);
        }

        // ---- Geometry ----

        int S(float value) => (int)Math.Round(value * _scale);
        Rectangle TitleRect => new Rectangle(S(16), S(14), _width - S(64), S(20));
        Rectangle MoreRect => new Rectangle(_width - S(44), S(8), S(32), S(32));

        Rectangle ScreenBox(Row row) => row.IsDevice ? row.Box : new Rectangle(row.Box.X, row.Box.Y - _scroll, row.Box.Width, row.Box.Height);

        // The ends of the slider track: Start is 0 %, End is 100 %.
        PointF TrackStart(Row row)
        {
            Rectangle box = ScreenBox(row);
            if (row.Vertical)
                return new PointF(box.Left + box.Width / 2f, box.Top + S(ColumnTrackTop + ColumnTrackLength));
            return new PointF(S(66), SliderY(row));
        }

        PointF TrackEnd(Row row)
        {
            Rectangle box = ScreenBox(row);
            if (row.Vertical)
                return new PointF(box.Left + box.Width / 2f, box.Top + S(ColumnTrackTop));
            return new PointF(_width - S(70), SliderY(row));
        }

        PointF Along(Row row, float fraction)
        {
            PointF start = TrackStart(row), end = TrackEnd(row);
            return new PointF(start.X + (end.X - start.X) * fraction, start.Y + (end.Y - start.Y) * fraction);
        }

        int SliderY(Row row) => ScreenBox(row).Top + (row.IsDevice ? S(22) : S(34));

        Rectangle IconRect(Row row)
        {
            if (row.Vertical)
            {
                Rectangle box = ScreenBox(row);
                return new Rectangle(box.Left + box.Width / 2 - S(16), box.Top + S(4), S(32), S(32));
            }
            return new Rectangle(S(12), SliderY(row) - S(16), S(32), S(32));
        }

        Rectangle LabelRect(Row row)
        {
            Rectangle box = ScreenBox(row);
            if (row.Vertical)
                return new Rectangle(box.Left + S(4), box.Top + S(196), box.Width - S(8), S(32));
            return new Rectangle(S(56), box.Top + S(6), _width - S(72), S(18));
        }

        Rectangle ValueRect(Row row)
        {
            if (row.Vertical)
            {
                Rectangle box = ScreenBox(row);
                return new Rectangle(box.Left, box.Top + S(176), box.Width, S(18));
            }
            return new Rectangle(_width - S(48), SliderY(row) - S(12), S(32), S(24));
        }

        Rectangle SliderHitRect(Row row)
        {
            PointF start = TrackStart(row), end = TrackEnd(row);
            if (row.Vertical)
                return Rectangle.FromLTRB((int)start.X - S(14), (int)end.Y - S(12), (int)start.X + S(14), (int)start.Y + S(12));
            return Rectangle.FromLTRB((int)start.X - S(12), (int)start.Y - S(14), (int)end.X + S(12), (int)start.Y + S(14));
        }

        Rectangle ScrollTrackRect => Rectangle.FromLTRB(_width - S(10), _appsTop + S(4), _width - S(2), _appsBottom - S(4));

        RectangleF ScrollThumbRect
        {
            get
            {
                Rectangle track = ScrollTrackRect;
                float viewport = _appsBottom - _appsTop;
                float thumb = Math.Max(S(24), track.Height * viewport / (viewport + _maxScroll));
                float top = track.Top + (track.Height - thumb) * _scroll / Math.Max(1, _maxScroll);
                return new RectangleF(track.Left, top, track.Width, thumb);
            }
        }

        bool InApps(Point point) => point.Y >= _appsTop && point.Y < _appsBottom;

        Row RowAt(Point point)
        {
            foreach (var row in _rows)
            {
                if (!row.IsDevice && !InApps(point))
                    continue;
                if (ScreenBox(row).Contains(point))
                    return row;
            }
            return null;
        }

        Part PartAt(Row row, Point point)
        {
            if (row == null)
                return Part.None;
            if (IconRect(row).Contains(point))
                return Part.Icon;
            return SliderHitRect(row).Contains(point) ? Part.Slider : Part.None;
        }

        Row FindRow(string key) => key == null ? null : _rows.Find(r => r.Key == key);

        // ---- Painting ----

        const TextFormatFlags LineFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Palette p = _palette;
            g.Clear(p.Background);
            if (!_dwmDrawsBorder)
            {
                using (var pen = new Pen(p.Border))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var devices = _rows.FindAll(r => r.IsDevice);
            string title = _snapshot == null ? "Looking for audio devices…"
                : devices.Count == 0 ? "No output device"
                : devices.Count == 1 ? devices[0].Label
                : "Volume mixer";
            TextRenderer.DrawText(g, title, _titleFont, TitleRect, p.Text, LineFlags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (_hoverMore || _pressMore)
            {
                using (var brush = new SolidBrush(_pressMore ? p.Pressed : p.Hover))
                    g.FillRoundedRectangle(brush, MoreRect, S(4));
            }
            TextRenderer.DrawText(g, Glyphs.More, _iconFont, MoreRect, p.Text, LineFlags | TextFormatFlags.HorizontalCenter);

            foreach (var device in devices)
                DrawControl(g, device);
            if (devices.Count > 0)
            {
                int lineY = _appsTop - S(SeparatorHeight) / 2 - 1;
                using (var pen = new Pen(p.Separator))
                    g.DrawLine(pen, S(16), lineY, _width - S(16), lineY);
            }

            g.SetClip(Rectangle.FromLTRB(0, _appsTop, _width, _appsBottom));
            bool anyApps = false;
            foreach (var row in _rows)
            {
                if (row.IsDevice)
                    continue;
                anyApps = true;
                Rectangle box = ScreenBox(row);
                if (box.Bottom > _appsTop && box.Top < _appsBottom)
                    DrawControl(g, row);
            }
            if (!anyApps && _snapshot != null)
            {
                var empty = new Rectangle(S(16), _appsTop, _width - S(32), S(EmptyHeight) - S(8));
                TextRenderer.DrawText(g, "No apps are using audio right now", _labelFont, empty, p.SubText, LineFlags | TextFormatFlags.Left);
            }
            g.ResetClip();

            if (_maxScroll > 0)
            {
                RectangleF thumb = ScrollThumbRect;
                float width = (_hoverScroll || _draggingScroll ? 5 : 3) * _scale;
                var bar = new RectangleF(thumb.Right - S(2) - width, thumb.Top, width, thumb.Height);
                using (var brush = new SolidBrush(Color.FromArgb(_hoverScroll || _draggingScroll ? 170 : 110, p.SubText)))
                    g.FillRoundedRectangle(brush, bar, width / 2);
            }
        }

        void DrawControl(Graphics g, Row row)
        {
            Palette p = _palette;
            Rectangle icon = IconRect(row);
            bool hoverIcon = _hoverKey == row.Key && _hoverPart == Part.Icon;
            bool pressIcon = _pressKey == row.Key;
            Color iconBackground = pressIcon ? p.Pressed : hoverIcon ? p.Hover : p.Background;
            if (iconBackground != p.Background)
            {
                using (var brush = new SolidBrush(iconBackground))
                    g.FillRoundedRectangle(brush, icon, S(4));
            }
            DrawIcon(g, row, icon, iconBackground);

            if (!row.IsDevice)
                TextRenderer.DrawText(g, row.Label, _labelFont, LabelRect(row), p.Text, LineFlags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            else if (row.Vertical)
                DrawDeviceName(g, row.Label, LabelRect(row));

            DrawSlider(g, row);

            var valueFlags = LineFlags | (row.Vertical ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Right);
            TextRenderer.DrawText(g, row.Level.Volume.ToString(), _valueFont, ValueRect(row), row.Level.Muted ? p.SubText : p.Text, valueFlags);

            if (_showFocus && _focusKey == row.Key)
            {
                Rectangle box = ScreenBox(row);
                var focus = row.Vertical
                    ? new RectangleF(box.Left + S(2), box.Top, box.Width - S(4), box.Height)
                    : new RectangleF(S(4), box.Top + S(2), _width - S(8), box.Height - S(4));
                using (var pen = new Pen(p.Text, 2 * _scale))
                using (var path = RoundedPath(focus, S(6)))
                    g.DrawPath(pen, path);
            }
        }

        // Device names look like "Headphones (Arctis Nova Pro Wireless)": the endpoint goes on the first line and the
        // hardware on the second, so narrow columns stay readable.
        void DrawDeviceName(Graphics g, string name, Rectangle bounds)
        {
            string first = name, second = null;
            int split = name.IndexOf(" (", StringComparison.Ordinal);
            if (split > 0)
            {
                first = name.Substring(0, split);
                second = name.Substring(split + 2).TrimEnd();
                if (second.EndsWith(")"))
                    second = second.Substring(0, second.Length - 1);
            }
            const TextFormatFlags flags = LineFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis;
            var top = new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height / 2);
            var bottom = new Rectangle(bounds.Left, top.Bottom, bounds.Width, bounds.Height - top.Height);
            TextRenderer.DrawText(g, first, _smallFont, top, _palette.Text, flags);
            if (second != null)
                TextRenderer.DrawText(g, second, _smallFont, bottom, _palette.SubText, flags);
        }

        void DrawSlider(Graphics g, Row row)
        {
            Palette p = _palette;
            Color fill = row.Level.Muted ? p.MutedFill : p.Accent;
            float thickness = 4 * _scale;
            PointF start = TrackStart(row), end = TrackEnd(row), thumb = Along(row, row.Level.Volume / 100f);

            using (var track = new Pen(p.Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(track, thumb, end);
            if (row.Level.Volume > 0)
            {
                using (var filled = new Pen(fill, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(filled, start, thumb);
                float peak = Math.Min(row.Peak, row.Level.Gain);
                if (!row.Level.Muted && peak > 0.01f)
                {
                    using (var meter = new Pen(p.Peak, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(meter, start, Along(row, peak));
                }
            }

            bool hover = (_hoverKey == row.Key && _hoverPart == Part.Slider) || _dragKey == row.Key;
            float outer = 10 * _scale;
            float inner = (_dragKey == row.Key ? 5 : hover ? 7 : 6) * _scale;
            using (var brush = new SolidBrush(p.ThumbOuter))
                g.FillEllipse(brush, thumb.X - outer, thumb.Y - outer, outer * 2, outer * 2);
            using (var pen = new Pen(p.ThumbBorder))
                g.DrawEllipse(pen, thumb.X - outer, thumb.Y - outer, outer * 2, outer * 2);
            using (var brush = new SolidBrush(fill))
                g.FillEllipse(brush, thumb.X - inner, thumb.Y - inner, inner * 2, inner * 2);
        }

        void DrawIcon(Graphics g, Row row, Rectangle bounds, Color background)
        {
            Palette p = _palette;
            bool speakerGlyph = (row.IsDevice && row.Icon == DeviceIcon.Speaker) || row.Kind == RowKind.SystemSounds;
            if (speakerGlyph)
            {
                // These glyphs show mute themselves.
                string glyph = row.IsDevice ? Glyphs.ForVolume(row.Level) : row.Level.Muted ? Glyphs.Mute : Glyphs.SystemSounds;
                TextRenderer.DrawText(g, glyph, _iconFont, bounds, p.Text, LineFlags | TextFormatFlags.HorizontalCenter);
                return;
            }

            int size = S(24);
            var image = new Rectangle(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size);
            if (row.IsDevice)
                TextRenderer.DrawText(g, Glyphs.ForDevice(row.Icon, row.Level), _iconFont, bounds, p.Text, LineFlags | TextFormatFlags.HorizontalCenter);
            else
            {
                Icon icon = AppIcons.Get(row.ImagePath, size);
                if (icon != null)
                    g.DrawIcon(icon, image);
                else
                {
                    using (var brush = new SolidBrush(p.MutedFill))
                        g.FillRoundedRectangle(brush, image, S(5));
                    string letter = string.IsNullOrEmpty(row.Label) ? "?" : row.Label.Substring(0, 1).ToUpperInvariant();
                    TextRenderer.DrawText(g, letter, _valueFont, image, Color.White, LineFlags | TextFormatFlags.HorizontalCenter);
                }
            }

            if (row.Level.Muted)
            {
                using (var veil = new SolidBrush(Color.FromArgb(150, background)))
                    g.FillRectangle(veil, image);
            }
            string badge = row.Level.Muted ? Glyphs.Mute : row.Kind == RowKind.DiscordSounds ? Glyphs.Bell : null;
            if (badge != null)
            {
                float d = 16 * _scale;
                var circle = new RectangleF(image.Right - d * 0.62f, image.Bottom - d * 0.62f, d, d);
                using (var brush = new SolidBrush(background))
                    g.FillEllipse(brush, circle);
                TextRenderer.DrawText(g, badge, _badgeFont, Rectangle.Round(circle), p.Text, LineFlags | TextFormatFlags.HorizontalCenter);
            }
        }

        static GraphicsPath RoundedPath(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---- Input ----

        void Change(Row row, Level level)
        {
            if (row.Level.Equals(level))
                return;
            row.Level = level;
            _pendingUntil[row.Key] = Environment.TickCount + PendingMilliseconds;
            Invalidate();
            LevelChanged?.Invoke(row.Key, level);
        }

        void Nudge(Row row, int delta)
        {
            if (row != null)
                Change(row, new Level(row.Level.Volume + delta, false));
        }

        void ToggleMute(Row row)
        {
            if (row != null)
                Change(row, new Level(row.Level.Volume, !row.Level.Muted));
        }

        void SetFromPoint(Row row, Point point)
        {
            PointF start = TrackStart(row), end = TrackEnd(row);
            float fraction = row.Vertical
                ? (start.Y - point.Y) / (start.Y - end.Y)
                : (point.X - start.X) / (end.X - start.X);
            Change(row, new Level((int)Math.Round(fraction * 100), false));
        }

        void ScrollToThumb(float thumbTop)
        {
            Rectangle track = ScrollTrackRect;
            float travel = track.Height - ScrollThumbRect.Height;
            ScrollTo(travel <= 0 ? 0 : (int)Math.Round((thumbTop - track.Top) / travel * _maxScroll));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;
            if (MoreRect.Contains(e.Location))
            {
                _pressMore = true;
                Invalidate();
                return;
            }
            if (_maxScroll > 0 && ScrollTrackRect.Contains(e.Location))
            {
                RectangleF thumb = ScrollThumbRect;
                if (e.Y < thumb.Top || e.Y >= thumb.Bottom)
                    ScrollToThumb(e.Y - thumb.Height / 2);
                _draggingScroll = true;
                _scrollGrab = e.Y - ScrollThumbRect.Top;
                Capture = true;
                Invalidate();
                return;
            }
            Row row = RowAt(e.Location);
            switch (PartAt(row, e.Location))
            {
                case Part.Slider:
                    _dragKey = row.Key;
                    Capture = true;
                    SetFromPoint(row, e.Location);
                    break;
                case Part.Icon:
                    _pressKey = row.Key;
                    break;
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingScroll)
            {
                ScrollToThumb(e.Y - _scrollGrab);
                return;
            }
            if (_dragKey != null)
            {
                Row dragged = FindRow(_dragKey);
                if (dragged != null)
                    SetFromPoint(dragged, e.Location);
                return;
            }
            bool hoverScroll = _maxScroll > 0 && ScrollTrackRect.Contains(e.Location);
            Row row = hoverScroll ? null : RowAt(e.Location);
            Part part = PartAt(row, e.Location);
            string key = part == Part.None ? null : row.Key;
            bool hoverMore = MoreRect.Contains(e.Location);
            if (key != _hoverKey || part != _hoverPart || hoverMore != _hoverMore || hoverScroll != _hoverScroll)
            {
                _hoverKey = key;
                _hoverPart = part;
                _hoverMore = hoverMore;
                _hoverScroll = hoverScroll;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingScroll)
            {
                _draggingScroll = false;
                Capture = false;
            }
            if (_dragKey != null)
            {
                _dragKey = null;
                Capture = false;
            }
            if (_pressKey != null)
            {
                Row released = RowAt(e.Location);
                if (released != null && released.Key == _pressKey && PartAt(released, e.Location) == Part.Icon)
                    ToggleMute(released);
                _pressKey = null;
            }
            if (_pressMore)
            {
                _pressMore = false;
                if (MoreRect.Contains(e.Location))
                {
                    MenuOpenedHere = true;
                    _menu.Show(this, new Point(MoreRect.Right, MoreRect.Bottom), ToolStripDropDownDirection.BelowLeft);
                }
            }
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && (_dragKey != null || _draggingScroll))
            {
                _dragKey = null;
                _draggingScroll = false;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_dragKey == null && !_draggingScroll && (_hoverKey != null || _hoverMore || _hoverScroll))
            {
                _hoverKey = null;
                _hoverMore = _hoverScroll = false;
                Invalidate();
            }
        }

        // The wheel only scrolls the app list; volumes change by dragging, clicking or the keyboard.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _wheelRemainder += e.Delta;
            int notches = _wheelRemainder / 120;
            _wheelRemainder %= 120;
            if (notches != 0 && _maxScroll > 0)
                ScrollTo(_scroll - notches * S(AppRowHeight));
        }

        void ScrollTo(int offset)
        {
            offset = Math.Max(0, Math.Min(offset, _maxScroll));
            if (offset == _scroll)
                return;
            _scroll = offset;
            _hoverKey = null;
            Invalidate();
        }

        void MoveFocus(int direction)
        {
            if (_rows.Count == 0)
                return;
            int index = _rows.FindIndex(r => r.Key == _focusKey);
            index = index < 0 ? 0 : Math.Max(0, Math.Min(_rows.Count - 1, index + (_showFocus ? direction : 0)));
            Row row = _rows[index];
            _focusKey = row.Key;
            _showFocus = true;
            if (!row.IsDevice)
            {
                Rectangle box = ScreenBox(row);
                if (box.Top < _appsTop)
                    ScrollTo(_scroll - (_appsTop - box.Top));
                else if (box.Bottom > _appsBottom)
                    ScrollTo(_scroll + (box.Bottom - _appsBottom));
            }
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Row focused = FindRow(_focusKey) ?? (_rows.Count > 0 ? _rows[0] : null);
            switch (keyData)
            {
                case Keys.Escape: HidePopup(); return true;
                case Keys.Up: MoveFocus(-1); return true;
                case Keys.Down:
                case Keys.Tab: MoveFocus(1); return true;
                case Keys.Shift | Keys.Tab: MoveFocus(-1); return true;
                case Keys.Left: Nudge(focused, -1); return true;
                case Keys.Right: Nudge(focused, 1); return true;
                case Keys.PageDown: Nudge(focused, -10); return true;
                case Keys.PageUp: Nudge(focused, 10); return true;
                case Keys.Home: if (focused != null) Change(focused, new Level(0, false)); return true;
                case Keys.End: if (focused != null) Change(focused, new Level(100, false)); return true;
                case Keys.Space:
                case Keys.M: ToggleMute(focused); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- DPI ----

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDpiChanged)
            {
                _scale = (m.WParam.ToInt64() & 0xFFFF) / 96f;
                RebuildFonts();
                _layoutSignature = null;
                Arrange(resizeWindow: true);
                Invalidate();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        void RebuildFonts()
        {
            DisposeFonts();
            _titleFont = new Font("Segoe UI Semibold", 14 * _scale, GraphicsUnit.Pixel);
            _labelFont = new Font("Segoe UI", 13 * _scale, GraphicsUnit.Pixel);
            _smallFont = new Font("Segoe UI", 12 * _scale, GraphicsUnit.Pixel);
            _valueFont = new Font("Segoe UI", 14 * _scale, GraphicsUnit.Pixel);
            _iconFont = new Font(Glyphs.FontName, 18 * _scale, GraphicsUnit.Pixel);
            _badgeFont = new Font(Glyphs.FontName, 10 * _scale, GraphicsUnit.Pixel);
        }

        void DisposeFonts()
        {
            _titleFont?.Dispose();
            _labelFont?.Dispose();
            _smallFont?.Dispose();
            _valueFont?.Dispose();
            _iconFont?.Dispose();
            _badgeFont?.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeFonts();
            base.Dispose(disposing);
        }

        static int DpiAt(Point point)
        {
            IntPtr monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, MonitorDefaultToNearest);
            return GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? (int)dpiX : 96;
        }

        const int WsExToolWindow = 0x80;
        const int CsDropShadow = 0x20000;
        const int WmDpiChanged = 0x02E0;
        const int DwmaWindowCornerPreference = 33;
        const int DwmaBorderColor = 34;
        const int DwmCornerRound = 2;
        const int MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint { public int X, Y; }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(NativePoint point, int flags);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
    }
}
