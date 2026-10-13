using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TrayMixer
{
    // Borderless flyout shown above the tray icon, painted by hand so it scales cleanly on any monitor.
    sealed class MixerPopup : Form
    {
        public const string MasterKey = "#master";

        const int BaseWidth = 360;
        const int HeaderHeight = 48;
        const int MasterRowHeight = 44;
        const int SeparatorHeight = 17;
        const int AppRowHeight = 52;
        const int EmptyHeight = 40;
        const int BottomPadding = 8;
        const int PendingMilliseconds = 400;
        const float PeakFalloff = 0.82f;

        enum Part { None, Icon, Slider }

        sealed class Row
        {
            public string Key;
            public RowKind Kind;
            public string Label;
            public string ImagePath;
            public Level Level;
            public float Peak;
            public int Top;
            public int Height;
            public bool IsMaster => Key == MasterKey;
        }

        readonly ContextMenuStrip _menu;
        readonly List<Row> _rows = new List<Row>();
        readonly Dictionary<string, int> _pendingUntil = new Dictionary<string, int>();
        MixerSnapshot _snapshot;
        Palette _palette = Palette.Current();
        float _scale = 1f;
        Font _titleFont, _labelFont, _valueFont, _iconFont, _badgeFont;
        bool _dwmDrawsBorder;
        Point _anchor;
        string _layoutSignature;
        int _appsTop, _appsBottom, _contentHeight, _scroll, _maxScroll;
        string _dragKey, _hoverKey, _pressKey, _focusKey = MasterKey;
        Part _hoverPart;
        bool _hoverMore, _pressMore, _showFocus, _menuFromPopup;
        int _wheelRemainder;

        public event Action<string, Level> LevelChanged;

        public int HiddenAtTick { get; private set; } = Environment.TickCount - 10000;

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
                if (!_menuFromPopup)
                    return;
                _menuFromPopup = false;
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
            if (snapshot.HasDevice)
                _rows.Add(new Row { Key = MasterKey, Level = snapshot.Master, Peak = snapshot.MasterPeak });
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

            if (Visible)
                Arrange(resizeWindow: true);
            Invalidate();
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
            _focusKey = MasterKey;
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
            _hoverMore = _pressMore = false;
            Hide();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!_menuFromPopup)
                HidePopup();
        }

        void Arrange(bool resizeWindow)
        {
            int y = S(HeaderHeight);
            foreach (var row in _rows)
            {
                if (!row.IsMaster)
                    continue;
                row.Top = y;
                row.Height = S(MasterRowHeight);
                y += row.Height + S(SeparatorHeight);
            }
            _appsTop = y;
            bool anyApps = false;
            foreach (var row in _rows)
            {
                if (row.IsMaster)
                    continue;
                row.Top = y;
                row.Height = S(AppRowHeight);
                y += row.Height;
                anyApps = true;
            }
            if (!anyApps)
                y += S(EmptyHeight);
            _contentHeight = y + S(BottomPadding);

            Rectangle work = Screen.FromPoint(_anchor).WorkingArea;
            int maxHeight = work.Height - 2 * S(12);
            int height = Math.Min(_contentHeight, maxHeight);
            _maxScroll = _contentHeight - height;
            _appsBottom = height - S(BottomPadding);
            _scroll = Math.Max(0, Math.Min(_scroll, _maxScroll));

            string signature = _rows.Count + "|" + height + "|" + _scale;
            if (resizeWindow && signature != _layoutSignature)
            {
                _layoutSignature = signature;
                Bounds = BoundsNear(_anchor, new Size(S(BaseWidth), height));
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
        Rectangle TitleRect => new Rectangle(S(16), S(14), Width - S(64), S(20));
        Rectangle MoreRect => new Rectangle(Width - S(44), S(8), S(32), S(32));
        int TrackLeft => S(66);
        int TrackRight => Width - S(70);
        int ScreenTop(Row row) => row.IsMaster ? row.Top : row.Top - _scroll;
        int SliderY(Row row) => ScreenTop(row) + (row.IsMaster ? S(22) : S(34));
        Rectangle IconRect(Row row) => new Rectangle(S(12), SliderY(row) - S(16), S(32), S(32));
        Rectangle LabelRect(Row row) => new Rectangle(S(56), ScreenTop(row) + S(6), Width - S(72), S(18));
        Rectangle ValueRect(Row row) => new Rectangle(Width - S(48), SliderY(row) - S(12), S(32), S(24));
        Rectangle SliderHitRect(Row row) => Rectangle.FromLTRB(TrackLeft - S(12), SliderY(row) - S(14), TrackRight + S(12), SliderY(row) + S(14));
        float ThumbX(Row row) => TrackLeft + (TrackRight - TrackLeft) * row.Level.Volume / 100f;

        Row RowAt(Point point)
        {
            foreach (var row in _rows)
            {
                if (!row.IsMaster && (point.Y < _appsTop || point.Y >= _appsBottom))
                    continue;
                int top = ScreenTop(row);
                if (point.Y >= top && point.Y < top + row.Height)
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

            string title = _snapshot == null ? "Looking for audio devices…" : _snapshot.DeviceName ?? "No output device";
            TextRenderer.DrawText(g, title, _titleFont, TitleRect, p.Text, LineFlags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (_hoverMore || _pressMore)
            {
                using (var brush = new SolidBrush(_pressMore ? p.Pressed : p.Hover))
                    g.FillRoundedRectangle(brush, MoreRect, S(4));
            }
            TextRenderer.DrawText(g, Glyphs.More, _iconFont, MoreRect, p.Text, LineFlags | TextFormatFlags.HorizontalCenter);

            Row master = FindRow(MasterKey);
            if (master != null)
            {
                DrawRow(g, master);
                int lineY = master.Top + master.Height + S(SeparatorHeight) / 2;
                using (var pen = new Pen(p.Separator))
                    g.DrawLine(pen, S(16), lineY, Width - S(16), lineY);
            }

            g.SetClip(Rectangle.FromLTRB(0, _appsTop, Width, _appsBottom));
            bool anyApps = false;
            foreach (var row in _rows)
            {
                if (row.IsMaster)
                    continue;
                anyApps = true;
                int top = ScreenTop(row);
                if (top + row.Height > _appsTop && top < _appsBottom)
                    DrawRow(g, row);
            }
            if (!anyApps && _snapshot != null)
            {
                var empty = new Rectangle(S(16), _appsTop, Width - S(32), S(EmptyHeight) - S(8));
                TextRenderer.DrawText(g, "No apps are using audio right now", _labelFont, empty, p.SubText, LineFlags | TextFormatFlags.Left);
            }
            g.ResetClip();

            if (_maxScroll > 0)
            {
                float viewport = _appsBottom - _appsTop;
                float content = viewport + _maxScroll;
                float thumbHeight = Math.Max(S(24), viewport * viewport / content);
                float thumbTop = _appsTop + (viewport - thumbHeight) * _scroll / _maxScroll;
                using (var brush = new SolidBrush(Color.FromArgb(110, p.SubText)))
                    g.FillRoundedRectangle(brush, new RectangleF(Width - S(6), thumbTop, S(3), thumbHeight), S(1.5f));
            }
        }

        const TextFormatFlags LineFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

        void DrawRow(Graphics g, Row row)
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
            DrawRowIcon(g, row, icon, iconBackground);

            if (row.Label != null)
                TextRenderer.DrawText(g, row.Label, _labelFont, LabelRect(row), p.Text, LineFlags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            Color fill = row.Level.Muted ? p.MutedFill : p.Accent;
            float cy = SliderY(row), thickness = 4 * _scale, x = ThumbX(row);
            using (var track = new Pen(p.Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(track, x, cy, TrackRight, cy);
            if (x > TrackLeft)
            {
                using (var filled = new Pen(fill, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(filled, TrackLeft, cy, x, cy);
                float peakX = TrackLeft + (TrackRight - TrackLeft) * Math.Min(row.Peak, row.Level.Gain);
                if (!row.Level.Muted && peakX > TrackLeft + 1)
                {
                    using (var peak = new Pen(p.Peak, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(peak, TrackLeft, cy, peakX, cy);
                }
            }

            bool hoverSlider = (_hoverKey == row.Key && _hoverPart == Part.Slider) || _dragKey == row.Key;
            float outer = 10 * _scale;
            float inner = (_dragKey == row.Key ? 5 : hoverSlider ? 7 : 6) * _scale;
            using (var brush = new SolidBrush(p.ThumbOuter))
                g.FillEllipse(brush, x - outer, cy - outer, outer * 2, outer * 2);
            using (var pen = new Pen(p.ThumbBorder))
                g.DrawEllipse(pen, x - outer, cy - outer, outer * 2, outer * 2);
            using (var brush = new SolidBrush(fill))
                g.FillEllipse(brush, x - inner, cy - inner, inner * 2, inner * 2);

            TextRenderer.DrawText(g, row.Level.Volume.ToString(), _valueFont, ValueRect(row), row.Level.Muted ? p.SubText : p.Text, LineFlags | TextFormatFlags.Right);

            if (_showFocus && _focusKey == row.Key)
            {
                var focus = new RectangleF(S(4), ScreenTop(row) + S(2), Width - S(8), row.Height - S(4));
                using (var pen = new Pen(p.Text, 2 * _scale))
                using (var path = RoundedPath(focus, S(6)))
                    g.DrawPath(pen, path);
            }
        }

        void DrawRowIcon(Graphics g, Row row, Rectangle bounds, Color background)
        {
            Palette p = _palette;
            if (row.IsMaster || row.Kind == RowKind.SystemSounds)
            {
                string glyph = row.IsMaster ? Glyphs.ForVolume(row.Level) : row.Level.Muted ? Glyphs.Mute : Glyphs.SystemSounds;
                TextRenderer.DrawText(g, glyph, _iconFont, bounds, p.Text, LineFlags | TextFormatFlags.HorizontalCenter);
                return;
            }

            int size = S(24);
            var image = new Rectangle(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size);
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

        void SetFromX(Row row, int x)
        {
            float fraction = (x - TrackLeft) / (float)(TrackRight - TrackLeft);
            Change(row, new Level((int)Math.Round(fraction * 100), false));
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
            Row row = RowAt(e.Location);
            switch (PartAt(row, e.Location))
            {
                case Part.Slider:
                    _dragKey = row.Key;
                    Capture = true;
                    SetFromX(row, e.X);
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
            if (_dragKey != null)
            {
                Row dragged = FindRow(_dragKey);
                if (dragged != null)
                    SetFromX(dragged, e.X);
                return;
            }
            Row row = RowAt(e.Location);
            Part part = PartAt(row, e.Location);
            string key = part == Part.None ? null : row.Key;
            bool hoverMore = MoreRect.Contains(e.Location);
            if (key != _hoverKey || part != _hoverPart || hoverMore != _hoverMore)
            {
                _hoverKey = key;
                _hoverPart = part;
                _hoverMore = hoverMore;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
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
                    _menuFromPopup = true;
                    _menu.Show(this, new Point(MoreRect.Right, MoreRect.Bottom), ToolStripDropDownDirection.BelowLeft);
                }
            }
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_dragKey != null && !Capture)
            {
                _dragKey = null;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_dragKey == null && (_hoverKey != null || _hoverMore))
            {
                _hoverKey = null;
                _hoverMore = false;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _wheelRemainder += e.Delta;
            int notches = _wheelRemainder / 120;
            _wheelRemainder %= 120;
            if (notches == 0)
                return;

            Row row = RowAt(e.Location);
            bool inApps = e.Y >= _appsTop && e.Y < _appsBottom;
            if (_maxScroll > 0 && inApps && PartAt(row, e.Location) != Part.Slider)
            {
                ScrollTo(_scroll - notches * S(AppRowHeight));
                return;
            }
            Nudge(row ?? FindRow(MasterKey), notches * 2);
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
            if (!row.IsMaster)
            {
                int top = ScreenTop(row);
                if (top < _appsTop)
                    ScrollTo(_scroll - (_appsTop - top));
                else if (top + row.Height > _appsBottom)
                    ScrollTo(_scroll + (top + row.Height - _appsBottom));
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
            _valueFont = new Font("Segoe UI", 14 * _scale, GraphicsUnit.Pixel);
            _iconFont = new Font(Glyphs.FontName, 18 * _scale, GraphicsUnit.Pixel);
            _badgeFont = new Font(Glyphs.FontName, 10 * _scale, GraphicsUnit.Pixel);
        }

        void DisposeFonts()
        {
            _titleFont?.Dispose();
            _labelFont?.Dispose();
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
