using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarMetrics
{
    sealed class WidgetForm : Form
    {
        readonly AppSettings _settings;
        Palette _palette;
        string _paletteSignature = "";

        readonly System.Windows.Forms.Timer _uiTimer;
        System.Threading.Timer _sampleTimer;
        MetricsSampler _sampler;
        MetricsSnapshot _snapshot;

        ContextMenuStrip _menu;
        StyleDialog _styleDialog;
        ToolStripMenuItem _autoStartItem;
        ToolStripMenuItem _followSystemItem;
        ToolStripMenuItem _transparentItem;
        ToolStripMenuItem _taskbarModeItem;
        ToolStripMenuItem _desktopModeItem;
        ToolStripMenuItem _desktopTopItem;
        ToolStripMenuItem _desktopBottomItem;
        ToolStripMenuItem _lockItem;
        ToolStripMenuItem[] _intervalItems;
        ToolStripMenuItem[] _alignItems;
        ToolStripMenuItem[] _columnGapItems;
        ToolStripMenuItem[] _visibilityItems;
        ToolStripMenuItem[] _imageFitItems;
        ToolStripMenuItem[] _imageDimItems;

        int _samplingFlag;
        int _taskbarCreatedMessage;
        bool _closing;
        bool _dragging;
        bool _dragMoved;
        bool _rendering;
        bool _hideLogged;
        Point _dragOffset;
        int _lastX, _lastY, _lastWidth, _lastHeight;
        bool _lastTopMost = true;
        bool _zOrderDirty = true;
        DateTime _lastPaletteCheck = DateTime.MinValue;

        Native.WinEventDelegate _foregroundHookDelegate;
        IntPtr _foregroundHook;
        Image _backgroundImage;
        string _loadedBackgroundPath = "";
        static bool _layeredErrorLogged;
        static bool _layeredSuccessLogged;
        static bool _renderLogged;

        public WidgetForm()
        {
            _settings = AppSettings.Load();
            _palette = ThemeService.Create(_settings);
            _paletteSignature = ThemeService.Signature(_palette);
            ReloadBackgroundImage();

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = _palette.Background;
            DoubleBuffered = true;
            ClientSize = new Size(430, 40);
            Text = "TaskbarMetrics";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer, true);

            BuildMenu();
            ContextMenuStrip = _menu;
            _menu.Opening += delegate { UpdateMenuState(); };

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 150;
            _uiTimer.Tick += OnUiTick;

            MouseDown += OnWidgetMouseDown;
            MouseMove += OnWidgetMouseMove;
            MouseUp += OnWidgetMouseUp;
            MouseDoubleClick += delegate
            {
                if (_settings.Locked) ShowStyleDialog();
            };
            MouseEnter += delegate { UpdateCursor(); };
            MouseLeave += delegate
            {
                if (!_dragging) Cursor = Cursors.Default;
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _taskbarCreatedMessage = (int)Native.RegisterWindowMessage("TaskbarCreated");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyPlacement(true);
            StartSampling();
            _uiTimer.Start();

            _foregroundHookDelegate = OnForegroundChanged;
            _foregroundHook = Native.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero,
                _foregroundHookDelegate, 0, 0, 0x0000);
            Native.RegisterHotKey(Handle, 0x4D54, 0x0003, 0x53); // Ctrl+Alt+S
        }

        protected override void WndProc(ref Message m)
        {
            if (_taskbarCreatedMessage != 0 && m.Msg == _taskbarCreatedMessage)
            {
                _lastWidth = 0;
                ApplyPlacement(true);
            }
            else if (m.Msg == 0x007E || m.Msg == 0x001A) // WM_DISPLAYCHANGE / WM_SETTINGCHANGE
            {
                _lastWidth = 0;
                _lastPaletteCheck = DateTime.MinValue;
                ApplyPlacement(true);
            }
            else if (m.Msg == 0x0312 && m.WParam.ToInt32() == 0x4D54) // WM_HOTKEY
            {
                ShowStyleDialog();
            }
            base.WndProc(ref m);
        }

        void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint threadId, uint time)
        {
            if (_closing || hwnd == IntPtr.Zero) return;
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return;
            if (hwnd == taskbar || Native.IsChild(taskbar, hwnd))
                ApplyPlacement(false);
        }

        void StartSampling()
        {
            try
            {
                _sampler = new MetricsSampler();
            }
            catch (Exception ex)
            {
                AppLog.Error("Sampler initialization failed", ex);
            }

            if (_sampler == null) return;
            _sampleTimer = new System.Threading.Timer(SampleTick, null, 300, _settings.RefreshMs);
        }

        void SampleTick(object state)
        {
            if (_closing) return;
            if (Interlocked.CompareExchange(ref _samplingFlag, 1, 0) != 0) return;
            try
            {
                if (_sampler == null) return;
                MetricsSnapshot snapshot = _sampler.Sample();
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action<MetricsSnapshot>(ApplySnapshot), snapshot);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Sampling failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _samplingFlag, 0);
            }
        }

        void ApplySnapshot(MetricsSnapshot snapshot)
        {
            if (_closing) return;
            _snapshot = snapshot;
            Redraw();
        }

        void OnUiTick(object sender, EventArgs e)
        {
            if (_closing) return;
            UpdatePaletteIfNeeded();
            ApplyPlacement(false);
        }

        void UpdatePaletteIfNeeded()
        {
            if (!_settings.FollowSystem) return;
            if ((DateTime.UtcNow - _lastPaletteCheck).TotalSeconds < 2) return;
            _lastPaletteCheck = DateTime.UtcNow;

            Palette palette = ThemeService.Create(_settings);
            string signature = ThemeService.Signature(palette);
            if (!string.Equals(signature, _paletteSignature, StringComparison.Ordinal))
            {
                _palette = palette;
                _paletteSignature = signature;
                Redraw();
            }
        }

        void RefreshPalette()
        {
            _palette = ThemeService.Create(_settings);
            _paletteSignature = ThemeService.Signature(_palette);
            Redraw();
        }

        void ReloadBackgroundImage()
        {
            string path = _settings.BackgroundImagePath ?? "";
            if (string.Equals(path, _loadedBackgroundPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (_backgroundImage != null)
            {
                _backgroundImage.Dispose();
                _backgroundImage = null;
            }
            _loadedBackgroundPath = path;

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                _backgroundImage = BackgroundImageStore.Load(path);
            }
            catch (Exception ex)
            {
                AppLog.Error("Loading background image failed", ex);
            }
        }

        // ---------------------------------------------------------------- placement

        static readonly int[] MetricBits = { 1, 2, 4, 8, 16, 32, 64 };
        const int CombinedNetworkMetric = 6;

        List<int> GetVisibleMetrics()
        {
            List<int> visible = new List<int>(7);
            int[] order = _settings.GetMetricOrder();
            for (int i = 0; i < order.Length; i++)
            {
                int metric = order[i];
                if ((_settings.VisibleMask & MetricBits[metric]) != 0) visible.Add(metric);
            }

            if (visible.Count == 0)
            {
                for (int i = 0; i < 6; i++) visible.Add(i);
            }
            return visible;
        }

        int[] ComputeColumnWidths(int height, List<int> visible, int cols)
        {
            int percentWidth = Math.Max(84, (int)Math.Round(height * 2.40));
            int rateWidth = Math.Max(104, (int)Math.Round(height * 3.00));
            int[] widths = new int[cols];
            for (int i = 0; i < visible.Count; i++)
            {
                int col = i % cols;
                int width = visible[i] >= 4 ? rateWidth : percentWidth;
                if (visible[i] == CombinedNetworkMetric) width += 12;
                if (width > widths[col]) widths[col] = width;
            }
            return widths;
        }

        // 最多两行：列数按数量均衡（4 项 2 列、5/6 项 3 列、7 项 4 列）。
        int ResolveColumns(int count, out int rows)
        {
            rows = count > 3 ? 2 : 1;
            return Math.Max(1, (count + rows - 1) / rows);
        }

        int ComputeBaseWidth(int height, out int cols, out int rows)
        {
            List<int> visible = GetVisibleMetrics();
            cols = ResolveColumns(visible.Count, out rows);
            int[] widths = ComputeColumnWidths(height, visible, cols);
            int total = 0;
            for (int i = 0; i < widths.Length; i++) total += widths[i];
            return total;
        }

        internal Size MeasurePanelSize(int height)
        {
            int cols, rows;
            int baseWidth = ComputeBaseWidth(height, out cols, out rows);
            return new Size(baseWidth + Clamp(_settings.ColumnGap, 0, 32) * (cols - 1), height);
        }

        bool TryGetTaskbar(out Native.RECT taskbarRect, out int trayAnchor)
        {
            taskbarRect = new Native.RECT();
            trayAnchor = 0;
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !Native.IsWindowVisible(taskbar)) return false;
            if (!Native.GetWindowRect(taskbar, out taskbarRect)) return false;

            int width = taskbarRect.Right - taskbarRect.Left;
            int height = taskbarRect.Bottom - taskbarRect.Top;
            if (width < 100 || height < 24) return false;
            if (width < height * 3) return false;

            IntPtr tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            Native.RECT trayRect;
            if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out trayRect))
            {
                trayAnchor = taskbarRect.Right - 160;
            }
            else
            {
                trayAnchor = trayRect.Left;
            }

            IntPtr dynamicContent = Native.FindWindowEx(taskbar, IntPtr.Zero, "DynamicContent2", null);
            if (dynamicContent != IntPtr.Zero && Native.IsWindowVisible(dynamicContent))
            {
                Native.RECT dynamicRect;
                if (Native.GetWindowRect(dynamicContent, out dynamicRect) &&
                    dynamicRect.Left > taskbarRect.Left + 100 &&
                    dynamicRect.Left < trayAnchor)
                {
                    trayAnchor = dynamicRect.Left;
                }
            }
            return trayAnchor > taskbarRect.Left + 20;
        }

        bool IsFullscreenActive()
        {
            int state;
            if (Native.SHQueryUserNotificationState(out state) != 0) return false;
            return state == 3 || state == 4;
        }

        bool IsAutoHiddenTaskbar(Native.RECT taskbarRect)
        {
            Native.APPBARDATA appBar = new Native.APPBARDATA();
            appBar.cbSize = (uint)Marshal.SizeOf(typeof(Native.APPBARDATA));
            int state = (int)Native.SHAppBarMessage(0x00000004, ref appBar);
            if ((state & Native.ABS_AUTOHIDE) == 0) return false;
            Rectangle screen = Screen.FromHandle(Native.FindWindow("Shell_TrayWnd", null)).Bounds;
            if (taskbarRect.Bottom <= screen.Top + 1 || taskbarRect.Top >= screen.Bottom - 1)
                return true;

            Native.APPBARDATA position = new Native.APPBARDATA();
            position.cbSize = (uint)Marshal.SizeOf(typeof(Native.APPBARDATA));
            if (Native.SHAppBarMessage(0x00000005, ref position) != IntPtr.Zero)
            {
                if (position.rc.Top >= screen.Bottom - 1 || position.rc.Bottom <= screen.Top + 1)
                    return true;
            }
            return false;
        }

        void ApplyPlacement(bool force)
        {
            if (_closing || IsDisposed || _dragging) return;

            int width, height, x, y;

            if (_settings.Mode == DisplayMode.Taskbar)
            {
                Native.RECT taskbarRect;
                int trayAnchor;
                bool taskbarReady = TryGetTaskbar(out taskbarRect, out trayAnchor);
                bool autoHidden = taskbarReady && IsAutoHiddenTaskbar(taskbarRect);
                bool fullscreen = taskbarReady && IsFullscreenActive();
                if (!taskbarReady || autoHidden || fullscreen)
                {
                    if (!_hideLogged)
                    {
                        _hideLogged = true;
                        AppLog.Info("Widget hidden: taskbar=" + taskbarReady
                            + " autoHide=" + autoHidden + " fullscreen=" + fullscreen);
                    }
                    HideWidget();
                    return;
                }

                int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
                height = Math.Max(28, (int)Math.Round(taskbarHeight * 0.80));
                int layoutCols, layoutRows;
                int baseWidth = ComputeBaseWidth(height, out layoutCols, out layoutRows);
                width = baseWidth + Clamp(_settings.ColumnGap, 0, 32) * (layoutCols - 1);
                int gap = Math.Max(4, height / 7);
                int minX = taskbarRect.Left + 8;
                int maxX = trayAnchor - gap - width;
                if (maxX < minX) maxX = minX;

                x = _settings.HasTaskbarX ? Clamp(_settings.TaskbarX, minX, maxX) : maxX;
                y = taskbarRect.Top + (taskbarHeight - height) / 2;

                if (_settings.HasTaskbarX && x != _settings.TaskbarX)
                {
                    _settings.TaskbarX = x;
                    _settings.Save();
                }
            }
            else
            {
                float scale = 1f;
                using (Graphics g = CreateGraphics())
                    scale = g.DpiX / 120f;
                if (scale < 0.75f) scale = 0.75f;
                if (scale > 2.5f) scale = 2.5f;

                height = Math.Max(30, (int)Math.Round(40 * scale));
                int layoutCols, layoutRows;
                int baseWidth = ComputeBaseWidth(height, out layoutCols, out layoutRows);
                width = baseWidth + Clamp(_settings.ColumnGap, 0, 32) * (layoutCols - 1);

                Rectangle work;
                if (_settings.DesktopX < 0 || _settings.DesktopY < 0)
                    work = Screen.PrimaryScreen.WorkingArea;
                else
                    work = Screen.FromPoint(new Point(_settings.DesktopX, _settings.DesktopY)).WorkingArea;

                if (_settings.DesktopX < 0 || _settings.DesktopY < 0)
                {
                    _settings.DesktopX = work.Left + (work.Width - width) / 2;
                    _settings.DesktopY = work.Bottom - height - 64;
                    _settings.Save();
                }

                x = Clamp(_settings.DesktopX, work.Left + 4, Math.Max(work.Left + 4, work.Right - width - 4));
                y = Clamp(_settings.DesktopY, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - height - 4));
                if (x != _settings.DesktopX || y != _settings.DesktopY)
                {
                    _settings.DesktopX = x;
                    _settings.DesktopY = y;
                    _settings.Save();
                }
            }

            bool topMost = _settings.Mode == DisplayMode.Taskbar || _settings.DesktopTopMost;
            if (TopMost != topMost)
            {
                TopMost = topMost;
                _zOrderDirty = true;
            }

            if (!Visible)
            {
                Show();
                _zOrderDirty = true;
            }

            bool moved = force || x != _lastX || y != _lastY ||
                width != _lastWidth || height != _lastHeight;
            if (moved)
            {
                Native.SetWindowPos(Handle, topMost ? Native.HWND_TOP : Native.HWND_BOTTOM,
                    x, y, width, height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                _lastX = x;
                _lastY = y;
                _lastWidth = width;
                _lastHeight = height;
                _lastTopMost = topMost;
                _zOrderDirty = false;
                Redraw();
            }
            else if (topMost)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOP, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            else if (_zOrderDirty || _lastTopMost != topMost)
            {
                Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                _zOrderDirty = false;
            }
            _lastTopMost = topMost;
        }

        void HideWidget()
        {
            if (Visible) Hide();
            _lastWidth = 0;
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ---------------------------------------------------------------- dragging

        void OnWidgetMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _settings.Locked) return;
            _dragging = true;
            _dragMoved = false;
            _dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
            Capture = true;
            Cursor = Cursors.SizeAll;
            Redraw();
        }

        void OnWidgetMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            Point cursor = Cursor.Position;
            int x = cursor.X - _dragOffset.X;
            int y = cursor.Y - _dragOffset.Y;
            _dragMoved = true;

            if (_settings.Mode == DisplayMode.Taskbar)
            {
                Native.RECT taskbarRect;
                int trayAnchor;
                if (!TryGetTaskbar(out taskbarRect, out trayAnchor)) return;
                int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
                int gap = Math.Max(4, ClientSize.Height / 7);
                int minX = taskbarRect.Left + 8;
                int maxX = trayAnchor - gap - ClientSize.Width;
                if (maxX < minX) maxX = minX;
                x = Clamp(x, minX, maxX);
                y = taskbarRect.Top + (taskbarHeight - ClientSize.Height) / 2;
                _settings.TaskbarX = x;
                _settings.HasTaskbarX = true;
            }
            else
            {
                Rectangle work = Screen.FromPoint(cursor).WorkingArea;
                x = Clamp(x, work.Left + 4, Math.Max(work.Left + 4, work.Right - ClientSize.Width - 4));
                y = Clamp(y, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - ClientSize.Height - 4));
                _settings.DesktopX = x;
                _settings.DesktopY = y;
            }

            Native.SetWindowPos(Handle, Native.HWND_TOP, x, y, ClientSize.Width, ClientSize.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            _lastX = x;
            _lastY = y;
            _zOrderDirty = true;
        }

        void OnWidgetMouseUp(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            UpdateCursor();
            if (_dragMoved) _settings.Save();
            Redraw();
            ApplyPlacement(true);
        }

        void UpdateCursor()
        {
            Cursor = _settings.Locked ? Cursors.Default : Cursors.SizeAll;
        }

        // ---------------------------------------------------------------- painting

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The surface is rendered with per-pixel alpha in OnPaint.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderLayered();
        }

        void Redraw()
        {
            RenderLayered();
        }

        void RenderLayered()
        {
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            if (width < 20 || height < 12 || !IsHandleCreated) return;
            ReloadBackgroundImage();
            if (!_renderLogged)
            {
                _renderLogged = true;
                AppLog.Info("RenderLayered called " + width + "x" + height);
            }
            if (_rendering) return;
            _rendering = true;
            try
            {
                using (Bitmap bitmap = BuildSurface(width, height))
                    UpdateLayeredSurface(bitmap);
            }
            finally
            {
                _rendering = false;
            }
        }

        // 把面板画到一张 32bpp 位图上；分层窗口和 --preview 导出都走这里。
        internal Bitmap BuildSurface(int width, int height)
        {
            ReloadBackgroundImage();
            Palette p = _palette;
            bool transparent = _settings.TransparentStyle;
            int radius = Math.Max(4, (int)Math.Round(height * 0.17));

            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.Clear(transparent ? Color.FromArgb(1, 0, 0, 0) : Color.Transparent);

                    if (!transparent)
                    {
                        RectangleF borderRect = new RectangleF(0.5f, 0.5f, width - 1f, height - 1f);
                        using (GraphicsPath path = RoundedRect(borderRect, radius))
                        {
                            using (SolidBrush fill = new SolidBrush(p.Background))
                                g.FillPath(fill, path);

                            if (_backgroundImage != null)
                            {
                                GraphicsState state = g.Save();
                                g.SetClip(path);
                                DrawBackgroundImage(g, _backgroundImage, width, height);
                                g.Restore(state);

                                int dim = Clamp(_settings.BackgroundImageDim, 0, 80);
                                if (dim > 0)
                                {
                                    using (SolidBrush overlay = new SolidBrush(
                                        Color.FromArgb(dim * 255 / 100, p.Background)))
                                        g.FillPath(overlay, path);
                                }
                            }

                            using (Pen pen = new Pen(p.Border, 1f))
                                g.DrawPath(pen, path);
                        }
                    }

                    int columnGap = Clamp(_settings.ColumnGap, 0, 32);
                    List<int> visible = GetVisibleMetrics();
                    int rows;
                    int cols = ResolveColumns(visible.Count, out rows);
                    int[] columnWidths = ComputeColumnWidths(height, visible, cols);
                    int[] columnStarts = new int[cols];
                    int cursor = 0;
                    for (int i = 0; i < cols; i++)
                    {
                        columnStarts[i] = cursor;
                        cursor += columnWidths[i] + columnGap;
                    }

                    int rowHeight = height / rows;
                    if (!transparent)
                    {
                        using (Pen divider = new Pen(p.Divider, 1f))
                        {
                            for (int i = 1; i < cols; i++)
                            {
                                float x = columnStarts[i] - columnGap / 2f + 0.5f;
                                g.DrawLine(divider, x, height * 0.18f, x, height * 0.82f);
                            }
                            if (rows > 1)
                            {
                                g.DrawLine(divider, width * 0.02f, rowHeight + 0.5f,
                                    width * 0.98f, rowHeight + 0.5f);
                            }
                        }
                    }

                    float labelPx = Math.Max(8f, (float)Math.Round(height * 0.32f));
                    float valuePx = Math.Max(9f, (float)Math.Round(height * 0.35f));
                    using (Font labelFont = new Font("Microsoft YaHei UI", labelPx,
                        FontStyle.Regular, GraphicsUnit.Pixel))
                    using (Font valueFont = new Font("Segoe UI", valuePx,
                        FontStyle.Bold, GraphicsUnit.Pixel))
                    using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
                    {
                        format.FormatFlags |= StringFormatFlags.NoClip;
                        string[] values = BuildValues(_snapshot);

                        for (int k = 0; k < visible.Count; k++)
                        {
                            int metric = visible[k];
                            int col = k % cols;
                            int row = k / cols;
                            int x = columnStarts[col];
                            int cellWidth = columnWidths[col];
                            int y = row * rowHeight;
                            int dotSize = Math.Max(3, height / 11);
                            int dotGap = 4;
                            int pad = Math.Max(6, height / 5);
                            bool arrowLabel = UsesArrowLabel(metric);
                            bool showDot = !transparent && metric < 4;
                            int dotWidth = showDot ? dotSize + dotGap : 0;
                            int centerY = y + rowHeight / 2;

                            if (metric == CombinedNetworkMetric)
                            {
                                DrawCombinedNetwork(g, p, values,
                                    _settings.GetMetricName(metric),
                                    _settings.IsDefaultMetricName(metric),
                                    x, cellWidth, y, rowHeight, pad,
                                    valueFont, labelFont, format);
                                continue;
                            }

                            int labelWidth = MeasureLabelWidth(g, labelFont, format, height, metric);
                            SizeF valueMeasure = g.MeasureString(values[metric], valueFont,
                                new PointF(0, 0), format);
                            int valueWidth = (int)Math.Ceiling(valueMeasure.Width) + 1;

                            // 列名固定在格子左侧；只有数值参与居中/靠右，间距由列宽和列间距体现。
                            int labelX = x + pad + dotWidth;
                            int labelRight = labelX + labelWidth;
                            int minGap = Math.Max(3, (int)Math.Round(height * 0.10));
                            int spanLeft = labelRight + minGap;
                            int spanRight = x + cellWidth - pad;
                            int valueX;
                            if (_settings.ValueAlign == 1)
                            {
                                valueX = spanRight - valueWidth;
                            }
                            else
                            {
                                int room = Math.Max(0, spanRight - spanLeft);
                                valueX = spanLeft + Math.Max(0, (room - valueWidth) / 2);
                            }
                            if (valueX < spanLeft) valueX = spanLeft;

                            if (showDot)
                            {
                                using (SolidBrush dotBrush = new SolidBrush(p.MetricColors[metric]))
                                    g.FillEllipse(dotBrush, x + pad, centerY - dotSize / 2f,
                                        dotSize, dotSize);
                            }

                            if (arrowLabel)
                            {
                                float arrowHeight = Math.Max(7f, height * 0.30f);
                                DrawArrowIcon(g, labelX + 1f, centerY - arrowHeight / 2f,
                                    arrowHeight, metric == 4, p.MetricColors[metric]);
                            }
                            else
                            {
                                float labelY = y + (rowHeight - labelFont.GetHeight(g)) / 2f;
                                using (SolidBrush labelBrush = new SolidBrush(p.Label))
                                    g.DrawString(_settings.GetMetricName(metric), labelFont,
                                        labelBrush, labelX, (float)Math.Round(labelY));
                            }

                            Color valueColor = p.Text;
                            if (metric < 4)
                            {
                                double numeric;
                                if (TryParsePercent(values[metric], out numeric))
                                {
                                    if (numeric >= 90) valueColor = p.Critical;
                                    else if (numeric >= 75) valueColor = p.Warning;
                                }
                            }

                            float valueY = y + (rowHeight - valueFont.GetHeight(g)) / 2f;
                            using (SolidBrush valueBrush = new SolidBrush(valueColor))
                                g.DrawString(values[metric], valueFont, valueBrush, valueX, valueY, format);
                        }
                    }

                    if (!_settings.Locked)
                    {
                        using (Pen accent = new Pen(p.Accent, 1.5f))
                        {
                            accent.DashStyle = DashStyle.Dash;
                            using (GraphicsPath path = RoundedRect(
                                new RectangleF(1f, 1f, width - 2f, height - 2f), radius))
                                g.DrawPath(accent, path);
                        }
                        using (SolidBrush grip = new SolidBrush(p.Accent))
                        {
                            int gx = width - 14;
                            int gy = 7;
                            g.FillRectangle(grip, gx, gy, 7, 2);
                            g.FillRectangle(grip, gx, gy + 4, 5, 2);
                        }
                    }
                }

                return bitmap;
            }
        }

        // 网络三个指标的默认名称用箭头图标；用户改过名称后按文字绘制。
        bool UsesArrowLabel(int metric)
        {
            return metric >= 4 && metric < CombinedNetworkMetric &&
                _settings.IsDefaultMetricName(metric);
        }

        int MeasureLabelWidth(Graphics g, Font labelFont, StringFormat format, int height, int metric)
        {
            if (UsesArrowLabel(metric))
                return Math.Max(9, (int)Math.Round(height * 0.30)) + 6;
            SizeF measure = g.MeasureString(_settings.GetMetricName(metric), labelFont,
                new PointF(0, 0), format);
            return (int)Math.Ceiling(measure.Width) + 2;
        }

        void DrawCombinedNetwork(Graphics g, Palette p, string[] values,
            string label, bool arrowLabel,
            int x, int width, int y, int rowHeight, int pad,
            Font valueFont, Font labelFont, StringFormat format)
        {
            float fontPx = Math.Max(7f, valueFont.Size * 0.68f);
            using (Font smallFont = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                float textHeight = smallFont.GetHeight(g);
                float centerY = y + rowHeight / 2f;
                float arrowHeight = Math.Max(6f, textHeight - 2f);
                float labelX = x + pad;

                if (arrowLabel)
                {
                    DrawArrowIcon(g, labelX, centerY - arrowHeight / 2f, arrowHeight,
                        true, p.MetricColors[4]);
                    labelX += 9f;
                    DrawArrowIcon(g, labelX, centerY - arrowHeight / 2f, arrowHeight,
                        false, p.MetricColors[5]);
                    labelX += 9f;
                }
                else
                {
                    float labelY = y + (rowHeight - labelFont.GetHeight(g)) / 2f;
                    using (SolidBrush labelBrush = new SolidBrush(p.Label))
                        g.DrawString(label, labelFont, labelBrush, labelX, labelY, format);
                }

                float valueRight = x + width - pad;
                float lineHeight = rowHeight / 2f;
                DrawCombinedValue(g, p, smallFont, format, values[4], valueRight,
                    y + (lineHeight - textHeight) / 2f);
                DrawCombinedValue(g, p, smallFont, format, values[5], valueRight,
                    y + lineHeight + (lineHeight - textHeight) / 2f);
            }
        }

        static void DrawCombinedValue(Graphics g, Palette p, Font font, StringFormat format,
            string value, float valueRight, float y)
        {
            SizeF size = g.MeasureString(value, font, new PointF(0, 0), format);
            using (SolidBrush brush = new SolidBrush(p.Text))
                g.DrawString(value, font, brush, valueRight - size.Width, y, format);
        }

        static void DrawArrowIcon(Graphics g, float x, float y, float height, bool up, Color color)
        {
            float centerX = x + 4f;
            float top = y;
            float bottom = y + height;
            using (Pen pen = new Pen(color, 1.6f))
            {
                g.DrawLine(pen, centerX, top, centerX, bottom);
                if (up)
                {
                    g.DrawLine(pen, centerX, top, centerX - 3f, top + 3f);
                    g.DrawLine(pen, centerX, top, centerX + 3f, top + 3f);
                }
                else
                {
                    g.DrawLine(pen, centerX, bottom, centerX - 3f, bottom - 3f);
                    g.DrawLine(pen, centerX, bottom, centerX + 3f, bottom - 3f);
                }
            }
        }

        void DrawBackgroundImage(Graphics g, Image image, int width, int height)
        {
            InterpolationMode oldInterpolation = g.InterpolationMode;
            PixelOffsetMode oldOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int fit = Clamp(_settings.BackgroundImageFit, 0, 3);
            if (fit == 1)
            {
                g.DrawImage(image, 0, 0, width, height);
            }
            else if (fit == 2)
            {
                float scale = Math.Min(width / (float)image.Width, height / (float)image.Height);
                int drawWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
                int drawHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
                g.DrawImage(image, (width - drawWidth) / 2, (height - drawHeight) / 2,
                    drawWidth, drawHeight);
            }
            else if (fit == 3)
            {
                using (TextureBrush brush = new TextureBrush(image, WrapMode.Tile))
                    g.FillRectangle(brush, 0, 0, width, height);
            }
            else
            {
                float scale = Math.Max(width / (float)image.Width, height / (float)image.Height);
                int drawWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
                int drawHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
                g.DrawImage(image, (width - drawWidth) / 2, (height - drawHeight) / 2,
                    drawWidth, drawHeight);
            }

            g.InterpolationMode = oldInterpolation;
            g.PixelOffsetMode = oldOffset;
        }

        void UpdateLayeredSurface(Bitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                if (!_layeredErrorLogged) AppLog.Info("UpdateLayeredSurface: GetDC failed");
                _layeredErrorLogged = true;
                return;
            }

            IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                if (!_layeredErrorLogged) AppLog.Info("UpdateLayeredSurface: CreateCompatibleDC failed");
                _layeredErrorLogged = true;
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                return;
            }

            Native.BITMAPINFO info = new Native.BITMAPINFO();
            info.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = 0;

            IntPtr bits;
            IntPtr dib = Native.CreateDIBSection(screenDc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                if (!_layeredErrorLogged) AppLog.Info("UpdateLayeredSurface: CreateDIBSection failed");
                _layeredErrorLogged = true;
                Native.DeleteDC(memoryDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                return;
            }

            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int bytes = width * 4;
                for (int y = 0; y < height; y++)
                {
                    Native.CopyMemory(new IntPtr(bits.ToInt64() + (long)y * bytes),
                        new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride),
                        (UIntPtr)(uint)bytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            IntPtr oldBitmap = Native.SelectObject(memoryDc, dib);
            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.BlendOp = 0;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1;

            Native.SIZE size = new Native.SIZE();
            size.CX = width;
            size.CY = height;
            Native.POINT destination = new Native.POINT();
            destination.X = Left;
            destination.Y = Top;
            Native.POINT source = new Native.POINT();
            bool ok = Native.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size,
                memoryDc, ref source, 0, ref blend, 0x00000002);
            if (!ok && !_layeredErrorLogged)
            {
                _layeredErrorLogged = true;
                AppLog.Info("UpdateLayeredWindow failed: " + Marshal.GetLastWin32Error());
            }
            else if (ok && !_layeredSuccessLogged)
            {
                _layeredSuccessLogged = true;
                AppLog.Info("UpdateLayeredWindow ok " + width + "x" + height);
            }

            Native.SelectObject(memoryDc, oldBitmap);
            Native.DeleteObject(dib);
            Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static string[] BuildValues(MetricsSnapshot s)
        {
            if (s == null)
                return new string[] { "--", "--", "--", "--", "--", "--" };
            return new string[]
            {
                FormatPercent(s.Cpu),
                FormatPercent(s.Gpu),
                FormatPercent(s.Memory),
                FormatPercent(s.Disk),
                FormatRate(s.NetUp),
                FormatRate(s.NetDown)
            };
        }

        static string FormatPercent(double value)
        {
            if (value < 0 || double.IsNaN(value) || double.IsInfinity(value)) return "--";
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond < 0 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
                return "--";
            if (bytesPerSecond < 1024)
                return Math.Round(bytesPerSecond).ToString("0", CultureInfo.InvariantCulture) + "B/s";
            if (bytesPerSecond < 1024 * 1024)
            {
                double kb = bytesPerSecond / 1024.0;
                if (kb < 10) return kb.ToString("0.0", CultureInfo.InvariantCulture) + "K/s";
                return Math.Round(kb).ToString("0", CultureInfo.InvariantCulture) + "K/s";
            }
            if (bytesPerSecond < 1024L * 1024 * 1024)
                return (bytesPerSecond / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "M/s";
            return (bytesPerSecond / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + "G/s";
        }

        static bool TryParsePercent(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text == "--") return false;
            string number = text.TrimEnd('%');
            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // ---------------------------------------------------------------- menu

        void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.ShowCheckMargin = true;

            ToolStripMenuItem intervalMenu = new ToolStripMenuItem("刷新间隔");
            _intervalItems = new ToolStripMenuItem[3];
            int[] intervals = { 1000, 2000, 5000 };
            string[] names = { "1 秒", "2 秒", "5 秒" };
            for (int i = 0; i < intervals.Length; i++)
            {
                int interval = intervals[i];
                ToolStripMenuItem item = new ToolStripMenuItem(names[i]);
                item.Tag = interval;
                item.Click += delegate { SetInterval(interval); };
                _intervalItems[i] = item;
                intervalMenu.DropDownItems.Add(item);
            }

            ToolStripMenuItem styleMenu = new ToolStripMenuItem("样式调整");
            ToolStripMenuItem styleDialogItem = new ToolStripMenuItem("样式设置...");
            styleDialogItem.Click += delegate { ShowStyleDialog(); };
            _followSystemItem = new ToolStripMenuItem("跟随系统颜色");
            _followSystemItem.Click += delegate { ToggleFollowSystem(); };
            _transparentItem = new ToolStripMenuItem("透明样式（仅显示文字）");
            _transparentItem.Click += delegate { ToggleTransparentStyle(); };

            ToolStripMenuItem alignMenu = new ToolStripMenuItem("数值对齐");
            _alignItems = new ToolStripMenuItem[2];
            string[] alignNames = { "居中", "向右对齐" };
            for (int i = 0; i < alignNames.Length; i++)
            {
                int align = i;
                ToolStripMenuItem item = new ToolStripMenuItem(alignNames[i]);
                item.Tag = align;
                item.Click += delegate { SetValueAlign(align); };
                _alignItems[i] = item;
                alignMenu.DropDownItems.Add(item);
            }

            ToolStripMenuItem columnGapMenu = new ToolStripMenuItem("列与列之间间距");
            _columnGapItems = new ToolStripMenuItem[7];
            int[] columnGaps = { 0, 4, 8, 12, 16, 24, 32 };
            for (int i = 0; i < columnGaps.Length; i++)
            {
                int gap = columnGaps[i];
                ToolStripMenuItem item = new ToolStripMenuItem(gap + " px");
                item.Tag = gap;
                item.Click += delegate { SetColumnGap(gap); };
                _columnGapItems[i] = item;
                columnGapMenu.DropDownItems.Add(item);
            }

            ToolStripMenuItem bgItem = new ToolStripMenuItem("背景颜色...");
            bgItem.Click += delegate { PickColor(0); };
            ToolStripMenuItem textItem = new ToolStripMenuItem("文字颜色...");
            textItem.Click += delegate { PickColor(1); };
            ToolStripMenuItem labelItem = new ToolStripMenuItem("标签颜色...");
            labelItem.Click += delegate { PickColor(2); };
            ToolStripMenuItem borderItem = new ToolStripMenuItem("边框颜色...");
            borderItem.Click += delegate { PickColor(3); };

            ToolStripMenuItem chooseImageItem = new ToolStripMenuItem("选择背景图片...");
            chooseImageItem.Click += delegate { ChooseBackgroundImage(); };
            ToolStripMenuItem clearImageItem = new ToolStripMenuItem("清除背景图片");
            clearImageItem.Click += delegate { ClearBackgroundImage(); };

            ToolStripMenuItem imageFitMenu = new ToolStripMenuItem("背景图片填充方式");
            _imageFitItems = new ToolStripMenuItem[4];
            string[] fitNames = { "填充裁剪", "拉伸", "完整显示", "平铺" };
            for (int i = 0; i < fitNames.Length; i++)
            {
                int fit = i;
                ToolStripMenuItem item = new ToolStripMenuItem(fitNames[i]);
                item.Tag = fit;
                item.Click += delegate { SetBackgroundImageFit(fit); };
                _imageFitItems[i] = item;
                imageFitMenu.DropDownItems.Add(item);
            }

            ToolStripMenuItem imageDimMenu = new ToolStripMenuItem("背景图片遮罩");
            _imageDimItems = new ToolStripMenuItem[5];
            int[] dims = { 0, 20, 40, 60, 80 };
            for (int i = 0; i < dims.Length; i++)
            {
                int dim = dims[i];
                ToolStripMenuItem item = new ToolStripMenuItem(dim + "%");
                item.Tag = dim;
                item.Click += delegate { SetBackgroundImageDim(dim); };
                _imageDimItems[i] = item;
                imageDimMenu.DropDownItems.Add(item);
            }

            ToolStripMenuItem resetStyleItem = new ToolStripMenuItem("恢复默认样式");
            resetStyleItem.Click += delegate
            {
                _settings.ResetStyle();
                _settings.Save();
                ReloadBackgroundImage();
                RefreshPalette();
                UpdateMenuState();
            };

            styleMenu.DropDownItems.Add(styleDialogItem);
            styleMenu.DropDownItems.Add(_followSystemItem);
            styleMenu.DropDownItems.Add(_transparentItem);
            styleMenu.DropDownItems.Add(new ToolStripSeparator());
            styleMenu.DropDownItems.Add(alignMenu);
            styleMenu.DropDownItems.Add(columnGapMenu);
            styleMenu.DropDownItems.Add(bgItem);
            styleMenu.DropDownItems.Add(textItem);
            styleMenu.DropDownItems.Add(labelItem);
            styleMenu.DropDownItems.Add(borderItem);
            styleMenu.DropDownItems.Add(new ToolStripSeparator());
            styleMenu.DropDownItems.Add(chooseImageItem);
            styleMenu.DropDownItems.Add(clearImageItem);
            styleMenu.DropDownItems.Add(imageFitMenu);
            styleMenu.DropDownItems.Add(imageDimMenu);
            styleMenu.DropDownItems.Add(new ToolStripSeparator());
            styleMenu.DropDownItems.Add(resetStyleItem);

            ToolStripMenuItem positionMenu = new ToolStripMenuItem("位置");
            _taskbarModeItem = new ToolStripMenuItem("任务栏模式");
            _taskbarModeItem.Click += delegate { SetMode(DisplayMode.Taskbar); };
            _desktopModeItem = new ToolStripMenuItem("屏幕浮窗模式");
            _desktopModeItem.Click += delegate { SetMode(DisplayMode.Desktop); };
            _desktopTopItem = new ToolStripMenuItem("页面置顶");
            _desktopTopItem.Click += delegate { SetDesktopTopMost(true); };
            _desktopBottomItem = new ToolStripMenuItem("页面置底");
            _desktopBottomItem.Click += delegate { SetDesktopTopMost(false); };
            _lockItem = new ToolStripMenuItem("固定位置");
            _lockItem.Click += delegate { ToggleLock(); };
            ToolStripMenuItem unlockItem = new ToolStripMenuItem("解锁并拖动");
            unlockItem.Click += delegate
            {
                _settings.Locked = false;
                _settings.Save();
                UpdateCursor();
                Redraw();
                UpdateMenuState();
            };
            ToolStripMenuItem resetPositionItem = new ToolStripMenuItem("重置位置");
            resetPositionItem.Click += delegate
            {
                _settings.ResetPosition();
                _settings.Save();
                UpdateCursor();
                ApplyPlacement(true);
                UpdateMenuState();
            };
            positionMenu.DropDownItems.Add(_taskbarModeItem);
            positionMenu.DropDownItems.Add(_desktopModeItem);
            positionMenu.DropDownItems.Add(new ToolStripSeparator());
            positionMenu.DropDownItems.Add(_desktopTopItem);
            positionMenu.DropDownItems.Add(_desktopBottomItem);
            positionMenu.DropDownItems.Add(new ToolStripSeparator());
            positionMenu.DropDownItems.Add(_lockItem);
            positionMenu.DropDownItems.Add(unlockItem);
            positionMenu.DropDownItems.Add(new ToolStripSeparator());
            positionMenu.DropDownItems.Add(resetPositionItem);

            _autoStartItem = new ToolStripMenuItem("开机自启动");
            _autoStartItem.Click += delegate { ToggleAutoStart(); };

            ToolStripMenuItem visibilityMenu = new ToolStripMenuItem("显示内容");
            _visibilityItems = new ToolStripMenuItem[7];
            string[] visibilityNames = { "CPU", "GPU", "内存", "磁盘", "上传 ↑", "下载 ↓", "上下行 ↑↓" };
            int[] visibilityTags = { 1, 2, 4, 8, 16, 32, 64 };
            for (int i = 0; i < visibilityNames.Length; i++)
            {
                int bits = visibilityTags[i];
                ToolStripMenuItem item = new ToolStripMenuItem(visibilityNames[i]);
                item.Tag = bits;
                item.Click += delegate { ToggleMetricVisibility(bits); };
                _visibilityItems[i] = item;
                visibilityMenu.DropDownItems.Add(item);
            }

            ToolStripMenuItem refreshItem = new ToolStripMenuItem("立即刷新");
            refreshItem.Click += delegate
            {
                if (_sampleTimer != null) _sampleTimer.Change(0, _settings.RefreshMs);
            };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { Close(); };

            ToolStripMenuItem settingsMenu = new ToolStripMenuItem("设置");
            settingsMenu.DropDownItems.Add(intervalMenu);
            settingsMenu.DropDownItems.Add(styleMenu);
            settingsMenu.DropDownItems.Add(positionMenu);
            settingsMenu.DropDownItems.Add(visibilityMenu);
            settingsMenu.DropDownItems.Add(new ToolStripSeparator());
            settingsMenu.DropDownItems.Add(_autoStartItem);

            _menu.Items.Add(settingsMenu);
            _menu.Items.Add(refreshItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);
        }

        void UpdateMenuState()
        {
            _autoStartItem.Checked = AutoStart.IsEnabled();
            _followSystemItem.Checked = _settings.FollowSystem;
            _transparentItem.Checked = _settings.TransparentStyle;
            _taskbarModeItem.Checked = _settings.Mode == DisplayMode.Taskbar;
            _desktopModeItem.Checked = _settings.Mode == DisplayMode.Desktop;
            _desktopTopItem.Checked = _settings.DesktopTopMost;
            _desktopBottomItem.Checked = !_settings.DesktopTopMost;
            _lockItem.Checked = _settings.Locked;

            for (int i = 0; i < _intervalItems.Length; i++)
                _intervalItems[i].Checked = (int)_intervalItems[i].Tag == _settings.RefreshMs;
            for (int i = 0; i < _alignItems.Length; i++)
                _alignItems[i].Checked = (int)_alignItems[i].Tag == _settings.ValueAlign;
            for (int i = 0; i < _columnGapItems.Length; i++)
                _columnGapItems[i].Checked = (int)_columnGapItems[i].Tag == _settings.ColumnGap;
            for (int i = 0; i < _visibilityItems.Length; i++)
                _visibilityItems[i].Checked =
                    (_settings.VisibleMask & (int)_visibilityItems[i].Tag) != 0;
            for (int i = 0; i < _imageFitItems.Length; i++)
                _imageFitItems[i].Checked = (int)_imageFitItems[i].Tag == _settings.BackgroundImageFit;
            for (int i = 0; i < _imageDimItems.Length; i++)
                _imageDimItems[i].Checked = (int)_imageDimItems[i].Tag == _settings.BackgroundImageDim;
        }

        void SetInterval(int ms)
        {
            _settings.RefreshMs = ms;
            _settings.Save();
            if (_sampleTimer != null) _sampleTimer.Change(0, ms);
            UpdateMenuState();
        }

        void SetValueAlign(int align)
        {
            _settings.ValueAlign = Clamp(align, 0, 1);
            _settings.Save();
            Redraw();
            UpdateMenuState();
        }

        void SetColumnGap(int gap)
        {
            _settings.ColumnGap = Clamp(gap, 0, 32);
            _settings.Save();
            ApplyPlacement(true);
            UpdateMenuState();
        }

        void ToggleMetricVisibility(int bits)
        {
            int mask = _settings.VisibleMask;
            if ((mask & bits) == bits)
            {
                if ((mask & ~bits & 0x7F) == 0) return;
                mask &= ~bits;
            }
            else
            {
                mask |= bits;
            }
            _settings.VisibleMask = mask & 0x7F;
            _settings.Save();
            ApplyPlacement(true);
            UpdateMenuState();
        }

        void ToggleFollowSystem()
        {
            _settings.FollowSystem = !_settings.FollowSystem;
            _settings.Save();
            RefreshPalette();
            UpdateMenuState();
        }

        void ToggleTransparentStyle()
        {
            _settings.TransparentStyle = !_settings.TransparentStyle;
            _settings.Save();
            Redraw();
            UpdateMenuState();
        }

        void ToggleLock()
        {
            _settings.Locked = !_settings.Locked;
            _settings.Save();
            UpdateCursor();
            Redraw();
            UpdateMenuState();
        }

        void SetDesktopTopMost(bool topMost)
        {
            _settings.DesktopTopMost = topMost;
            _settings.Save();
            _zOrderDirty = true;
            ApplyPlacement(true);
            UpdateMenuState();
        }

        void ToggleAutoStart()
        {
            try
            {
                AutoStart.SetEnabled(!AutoStart.IsEnabled());
            }
            catch (Exception ex)
            {
                AppLog.Error("Auto start change failed", ex);
                MessageBox.Show("无法修改开机自启动：\n" + ex.Message, "任务栏性能",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateMenuState();
        }

        void SetMode(DisplayMode mode)
        {
            if (_settings.Mode == mode)
            {
                UpdateMenuState();
                return;
            }

            if (mode == DisplayMode.Desktop)
            {
                _settings.DesktopX = Left;
                _settings.DesktopY = Top;
            }
            else
            {
                _settings.HasTaskbarX = true;
                _settings.TaskbarX = Left;
            }
            _settings.Mode = mode;
            // 换模式时先解锁，方便立刻把面板拖到想要的位置。
            _settings.Locked = false;
            _settings.Save();
            _zOrderDirty = true;
            UpdateCursor();
            ApplyPlacement(true);
            UpdateMenuState();
        }

        void PickColor(int target)
        {
            Color current;
            if (target == 0) current = _settings.Background;
            else if (target == 1) current = _settings.TextColor;
            else if (target == 2) current = _settings.LabelColor;
            else current = _settings.BorderColor;

            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = current;
                dialog.FullOpen = true;
                dialog.AnyColor = true;
                dialog.CustomColors = new int[] { current.ToArgb() };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _settings.FollowSystem = false;
                    if (target == 0) _settings.Background = dialog.Color;
                    else if (target == 1) _settings.TextColor = dialog.Color;
                    else if (target == 2) _settings.LabelColor = dialog.Color;
                    else _settings.BorderColor = dialog.Color;
                    _settings.Save();
                    RefreshPalette();
                    UpdateMenuState();
                }
            }
        }

        void ChooseBackgroundImage()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择背景图片";
                dialog.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string imported = BackgroundImageStore.Import(dialog.FileName);
                    _settings.BackgroundImagePath = imported;
                    _settings.TransparentStyle = false;
                    _settings.Save();
                    ReloadBackgroundImage();
                    Redraw();
                    UpdateMenuState();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Importing background image failed: " + dialog.FileName, ex);
                    MessageBox.Show("加载背景图片失败：\n" + ex.Message, "任务栏性能",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        void ClearBackgroundImage()
        {
            BackgroundImageStore.Delete(_settings.BackgroundImagePath);
            _settings.BackgroundImagePath = "";
            _settings.Save();
            ReloadBackgroundImage();
            Redraw();
            UpdateMenuState();
        }

        void SetBackgroundImageFit(int fit)
        {
            _settings.BackgroundImageFit = Clamp(fit, 0, 3);
            _settings.Save();
            Redraw();
            UpdateMenuState();
        }

        void SetBackgroundImageDim(int dim)
        {
            _settings.BackgroundImageDim = Clamp(dim, 0, 80);
            _settings.Save();
            Redraw();
            UpdateMenuState();
        }

        void ShowStyleDialog()
        {
            if (_styleDialog != null && !_styleDialog.IsDisposed)
            {
                _styleDialog.Activate();
                return;
            }

            _styleDialog = new StyleDialog(_settings, OnStyleChanged, ResetPositionFromDialog,
                delegate { return _snapshot; });
            _styleDialog.FormClosed += delegate
            {
                _styleDialog = null;
                RefreshPalette();
                UpdateCursor();
                ApplyPlacement(true);
                UpdateMenuState();
            };
            _styleDialog.Show();
            _styleDialog.Activate();
        }

        void ResetPositionFromDialog()
        {
            _settings.ResetPosition();
            _settings.Save();
            UpdateCursor();
            ApplyPlacement(true);
        }

        void OnStyleChanged()
        {
            ReloadBackgroundImage();
            RefreshPalette();
            UpdateCursor();
            ApplyPlacement(false);
            UpdateMenuState();
        }

        // 供设置窗口的“关于”页显示当前运行数据。
        internal static string BuildDetailsText(MetricsSnapshot s)
        {
            if (s == null) return "（等待采样数据…）";
            StringBuilder sb = new StringBuilder();
            sb.Append("CPU ").Append(FormatPercent(s.Cpu));
            sb.Append("    GPU ").Append(FormatPercent(s.Gpu));
            sb.AppendLine();
            sb.Append("内存 ").Append(FormatPercent(s.Memory));
            if (s.TotalMemory > 0)
            {
                double totalGb = s.TotalMemory / 1073741824.0;
                double usedGb = (s.TotalMemory - s.AvailableMemory) / 1073741824.0;
                sb.Append("  ");
                sb.Append(usedGb.ToString("0.0", CultureInfo.InvariantCulture));
                sb.Append(" / ");
                sb.Append(totalGb.ToString("0.0", CultureInfo.InvariantCulture));
                sb.Append(" GB");
            }
            sb.AppendLine();
            sb.Append("磁盘 ").Append(FormatPercent(s.Disk));
            sb.Append("    读 ").Append(FormatRate(s.DiskRead));
            sb.Append("    写 ").Append(FormatRate(s.DiskWrite));
            sb.AppendLine();
            sb.Append("网络 ").Append(string.IsNullOrEmpty(s.NetworkName) ? "无活动网卡" : s.NetworkName);
            sb.AppendLine();
            sb.Append("  ↑ ").Append(FormatRate(s.NetUp));
            sb.Append("    ↓ ").Append(FormatRate(s.NetDown));
            sb.AppendLine();
            TimeSpan up = TimeSpan.FromSeconds(s.UptimeSeconds);
            sb.Append("运行时间 ").Append((int)up.TotalDays).Append("天 ");
            sb.Append(up.Hours.ToString("00")).Append(":");
            sb.Append(up.Minutes.ToString("00"));
            return sb.ToString();
        }

        internal MetricsSnapshot CurrentSnapshot
        {
            get { return _snapshot; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            if (_foregroundHook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            Native.UnregisterHotKey(Handle, 0x4D54);
            _uiTimer.Stop();
            _uiTimer.Dispose();
            if (_sampleTimer != null)
            {
                _sampleTimer.Dispose();
                _sampleTimer = null;
            }
            if (_sampler != null)
            {
                _sampler.Dispose();
                _sampler = null;
            }
            if (_styleDialog != null && !_styleDialog.IsDisposed)
            {
                _styleDialog.Close();
                _styleDialog = null;
            }
            if (_backgroundImage != null)
            {
                _backgroundImage.Dispose();
                _backgroundImage = null;
            }
            if (_menu != null) _menu.Dispose();
            base.OnFormClosing(e);
        }
    }
}
