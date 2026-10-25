using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TaskbarMetrics
{
    // 排序用的点阵握把：自绘，避免 “≡” 之类字符在不同字体下糊成一团。
    sealed class GripHandle : Control
    {
        public GripHandle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.SizeNS;
            BackColor = Color.FromArgb(234, 236, 239);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (SolidBrush brush = new SolidBrush(ForeColor))
            {
                int centerX = Width / 2;
                int centerY = Height / 2;
                for (int row = -1; row <= 1; row++)
                {
                    for (int col = 0; col < 2; col++)
                    {
                        e.Graphics.FillRectangle(brush,
                            centerX - 5 + col * 7, centerY - 7 + row * 7, 3, 3);
                    }
                }
            }
        }
    }

    sealed class StyleDialog : Form
    {
        const int ContentWidth = 520;

        readonly AppSettings _live;
        readonly AppSettings _draft;
        AppSettings _saved;
        readonly Action _changed;
        readonly Action _resetPosition;
        readonly Func<MetricsSnapshot> _snapshotProvider;

        CheckBox _autoStartCheck;
        Label _dirLabel;
        CheckBox _follow;
        CheckBox _transparent;
        CheckBox[] _visibilityChecks;
        TextBox[] _nameBoxes;
        Panel[] _rows;
        FlowLayoutPanel _orderList;
        Panel _dragRow;
        int _dragStartY;
        int _dragStep;
        Button _backgroundButton;
        Button _textButton;
        Button _labelButton;
        Button _borderButton;
        Label _imagePathLabel;
        ComboBox _imageFitCombo;
        TrackBar _imageDimSlider;
        Label _imageDimValue;
        RadioButton _alignCenter;
        RadioButton _alignRight;
        TrackBar _columnGapSlider;
        Label _columnGapValue;
        Label _aboutLabel;
        Label _disclaimer;
        RadioButton _taskbarMode;
        RadioButton _desktopMode;
        RadioButton _desktopTopRadio;
        RadioButton _desktopBottomRadio;
        CheckBox _lockCheck;

        readonly List<Button> _navButtons = new List<Button>();
        readonly List<Control> _pages = new List<Control>();
        Panel _pageHost;
        readonly List<Button> _saveButtons = new List<Button>();
        bool _loading;
        bool _dirty;

        static readonly Color NavBack = Color.FromArgb(238, 240, 243);
        static readonly Color NavIdleText = Color.FromArgb(48, 48, 48);
        static readonly Color NavActive = Color.FromArgb(0, 120, 212);
        static readonly Color HintColor = Color.FromArgb(104, 104, 104);

        public StyleDialog(AppSettings settings, Action changed, Action resetPosition)
            : this(settings, changed, resetPosition, null)
        {
        }

        public StyleDialog(AppSettings settings, Action changed, Action resetPosition,
            Func<MetricsSnapshot> snapshotProvider)
        {
            _live = settings;
            _draft = settings.Clone();
            _saved = settings.Clone();
            _changed = changed;
            _resetPosition = resetPosition;
            _snapshotProvider = snapshotProvider;

            Text = "设置";
            Font = new Font("Microsoft YaHei UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(740, 620);
            BackColor = Color.FromArgb(248, 248, 248);
            AutoScaleMode = AutoScaleMode.None;

            BuildUi();
            RefreshAll();
            SelectPage(0);
        }

        // ---------------------------------------------------------------- layout

        void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.RowCount = 2;
            root.Margin = new Padding(0);
            root.Padding = new Padding(0);
            root.BackColor = BackColor;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));
            Controls.Add(root);

            root.Controls.Add(BuildNav(), 0, 0);
            root.Controls.Add(BuildPageHost(), 1, 0);

            Control footer = BuildFooter();
            root.Controls.Add(footer, 0, 1);
            root.SetColumnSpan(footer, 2);
        }

        Control BuildNav()
        {
            Panel nav = new Panel();
            nav.Dock = DockStyle.Fill;
            nav.BackColor = NavBack;
            nav.Margin = new Padding(0);

            FlowLayoutPanel list = new FlowLayoutPanel();
            list.Dock = DockStyle.Fill;
            list.FlowDirection = FlowDirection.TopDown;
            list.WrapContents = false;
            list.AutoScroll = true;
            list.Padding = new Padding(10, 14, 10, 10);
            list.BackColor = NavBack;
            nav.Controls.Add(list);

            string[] titles = { "基础设置", "外观", "显示内容", "排版", "位置", "关于" };
            for (int i = 0; i < titles.Length; i++)
            {
                int index = i;
                Button button = new Button();
                button.Text = titles[i];
                button.Width = 112;
                button.Height = 38;
                button.Margin = new Padding(0, 0, 0, 6);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
                button.TextAlign = ContentAlignment.MiddleLeft;
                button.Padding = new Padding(14, 0, 0, 0);
                button.Cursor = Cursors.Hand;
                button.BackColor = NavBack;
                button.ForeColor = NavIdleText;
                button.Click += delegate { SelectPage(index); };
                _navButtons.Add(button);
                list.Controls.Add(button);
            }

            return nav;
        }

        Control BuildPageHost()
        {
            _pageHost = new Panel();
            _pageHost.Dock = DockStyle.Fill;
            _pageHost.AutoScroll = true;
            _pageHost.Padding = new Padding(20, 12, 12, 12);
            _pageHost.BackColor = BackColor;

            _pages.Add(BuildBasicPage());
            _pages.Add(BuildAppearancePage());
            _pages.Add(BuildContentPage());
            _pages.Add(BuildLayoutPage());
            _pages.Add(BuildPositionPage());
            _pages.Add(BuildAboutPage());

            foreach (Control page in _pages) _pageHost.Controls.Add(page);
            return _pageHost;
        }

        Control BuildFooter()
        {
            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Color.FromArgb(244, 244, 244);
            footer.Margin = new Padding(0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(16, 12, 16, 12);
            buttons.BackColor = footer.BackColor;
            footer.Controls.Add(buttons);

            Button close = new Button();
            close.Text = "关闭";
            close.Width = 96;
            close.Height = 32;
            close.FlatStyle = FlatStyle.System;
            close.Click += delegate { Close(); };
            buttons.Controls.Add(close);

            // 固定的置底免责声明，所有页面都显示。
            _disclaimer = new Label();
            _disclaimer.Text = "仅供学习交流，严禁用于商业用途，请于 24 小时内删除";
            _disclaimer.AutoSize = true;
            _disclaimer.Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
            _disclaimer.ForeColor = Color.FromArgb(140, 140, 140);
            _disclaimer.Margin = new Padding(0, 9, 0, 0);
            buttons.Controls.Add(_disclaimer);

            return footer;
        }

        internal void ShowPage(int index)
        {
            SelectPage(index < 0 || index >= _pages.Count ? 0 : index);
        }

        void SelectPage(int index)
        {
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].Visible = i == index;

            for (int i = 0; i < _navButtons.Count; i++)
            {
                bool active = i == index;
                _navButtons[i].BackColor = active ? NavActive : NavBack;
                _navButtons[i].ForeColor = active ? Color.White : NavIdleText;
                _navButtons[i].Font = new Font("Microsoft YaHei UI", 9.75f,
                    active ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            }

            if (_pageHost != null) _pageHost.AutoScrollPosition = new Point(0, 0);
            if (index == _pages.Count - 1) RefreshAboutText();
        }

        FlowLayoutPanel NewPage()
        {
            FlowLayoutPanel page = new FlowLayoutPanel();
            page.FlowDirection = FlowDirection.TopDown;
            page.WrapContents = false;
            page.AutoSize = true;
            page.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            page.Dock = DockStyle.Top;
            page.Margin = new Padding(0);
            page.Padding = new Padding(0);
            page.BackColor = BackColor;
            return page;
        }

        // ---------------------------------------------------------------- pages

        Control BuildBasicPage()
        {
            FlowLayoutPanel page = NewPage();
            page.Controls.Add(Header("启动"));

            _autoStartCheck = new CheckBox();
            _autoStartCheck.Text = "开机自启动（登录 Windows 后自动运行）";
            _autoStartCheck.AutoSize = true;
            _autoStartCheck.Margin = new Padding(0, 0, 0, 8);
            _autoStartCheck.CheckedChanged += delegate
            {
                if (_loading) return;
                MarkDirty();
            };
            page.Controls.Add(_autoStartCheck);
            page.Controls.Add(Hint("开机自启动在点“保存”后写入系统启动项。"));

            page.Controls.Add(Header("配置文件位置"));

            _dirLabel = new Label();
            _dirLabel.AutoSize = false;
            _dirLabel.Width = ContentWidth;
            _dirLabel.Height = 24;
            _dirLabel.AutoEllipsis = true;
            _dirLabel.TextAlign = ContentAlignment.MiddleLeft;
            _dirLabel.ForeColor = Color.FromArgb(64, 64, 64);
            _dirLabel.Margin = new Padding(0, 0, 0, 6);
            page.Controls.Add(_dirLabel);

            FlowLayoutPanel dirRow = new FlowLayoutPanel();
            dirRow.FlowDirection = FlowDirection.LeftToRight;
            dirRow.WrapContents = false;
            dirRow.AutoSize = true;
            dirRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            dirRow.Margin = new Padding(0, 0, 0, 6);
            Button pickDir = new Button();
            pickDir.Text = "选择文件夹...";
            pickDir.Width = 118;
            pickDir.Height = 30;
            pickDir.Margin = new Padding(0, 0, 8, 0);
            pickDir.FlatStyle = FlatStyle.System;
            pickDir.Click += delegate { ChooseDataDirectory(); };
            dirRow.Controls.Add(pickDir);
            Button openDir = new Button();
            openDir.Text = "打开目录";
            openDir.Width = 92;
            openDir.Height = 30;
            openDir.FlatStyle = FlatStyle.System;
            openDir.Click += delegate { OpenDataDirectory(); };
            dirRow.Controls.Add(openDir);
            page.Controls.Add(dirRow);

            page.Controls.Add(Hint("settings.ini、日志和背景图都放在这里。"));
            page.Controls.Add(Hint("换目录后点“保存”才真正切换。"));

            page.Controls.Add(Header("重置"));
            FlowLayoutPanel resetRow = new FlowLayoutPanel();
            resetRow.FlowDirection = FlowDirection.LeftToRight;
            resetRow.WrapContents = false;
            resetRow.AutoSize = true;
            resetRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            resetRow.Margin = new Padding(0, 0, 0, 6);
            Button resetAll = new Button();
            resetAll.Text = "恢复默认设置";
            resetAll.Width = 128;
            resetAll.Height = 30;
            resetAll.FlatStyle = FlatStyle.System;
            resetAll.Click += delegate
            {
                DialogResult answer = MessageBox.Show(
                    "会把配色、显示内容、名称、顺序、排版、配置文件目录恢复为出厂设置。\n保存后生效，面板位置保持不变。\n\n继续吗？",
                    "恢复默认设置", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (answer != DialogResult.OK) return;
                _draft.ResetToDefaults();
                MarkDirty();
                RefreshAll();
            };
            resetRow.Controls.Add(resetAll);
            page.Controls.Add(resetRow);
            page.Controls.Add(Hint("恢复默认会把配置目录改回默认位置。"));

            AppendSaveRow(page);
            return page;
        }

        void ChooseDataDirectory()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择配置文件存放目录";
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = _draft.DataDirectory;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string dir = dialog.SelectedPath;
                if (!CanWrite(dir)) return;
                _draft.DataDirectory = dir;
                _dirLabel.Text = dir;
                MarkDirty();
            }
        }

        bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, "taskbar-metrics-write-test.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Config directory not writable: " + dir, ex);
                MessageBox.Show("这个目录不可写：\n" + dir + "\n\n" + ex.Message
                    + "\n\n常见原因：安全软件或系统「受控文件夹访问」拦截了本程序对桌面/文档等位置的写入。"
                    + "\n可以换一个普通目录（例如 D:\\Tools\\TaskbarMetrics），或在安全软件里放行本程序。"
                    + "\n目录没有改动，仍是原来的位置。",
                    "任务栏性能", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        void OpenDataDirectory()
        {
            try
            {
                Directory.CreateDirectory(_draft.DataDirectory);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + _draft.DataDirectory + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开目录：\n" + ex.Message, "任务栏性能",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        Control BuildAppearancePage()
        {
            FlowLayoutPanel page = NewPage();
            page.Controls.Add(Header("配色"));

            _follow = new CheckBox();
            _follow.Text = "跟随系统颜色（自动匹配任务栏深浅色与系统强调色）";
            _follow.AutoSize = true;
            _follow.Margin = new Padding(0, 0, 0, 6);
            _follow.CheckedChanged += delegate
            {
                if (_loading) return;
                _draft.FollowSystem = _follow.Checked;
                MarkDirty();
                RefreshColorButtons();
            };
            page.Controls.Add(_follow);

            _transparent = new CheckBox();
            _transparent.Text = "透明样式（只显示文字，不显示背景、边框和分隔线）";
            _transparent.AutoSize = true;
            _transparent.Margin = new Padding(0, 0, 0, 8);
            _transparent.CheckedChanged += delegate
            {
                if (_loading) return;
                _draft.TransparentStyle = _transparent.Checked;
                MarkDirty();
                UpdateColorControlState();
            };
            page.Controls.Add(_transparent);

            page.Controls.Add(Hint("预置配色"));

            FlowLayoutPanel presets = new FlowLayoutPanel();
            presets.FlowDirection = FlowDirection.LeftToRight;
            presets.WrapContents = true;
            presets.AutoSize = true;
            presets.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            presets.Width = ContentWidth;
            presets.Margin = new Padding(0, 0, 0, 10);
            AddPreset(presets, "深色", Color.FromArgb(31, 31, 31), Color.FromArgb(242, 242, 242),
                Color.FromArgb(162, 162, 162), Color.FromArgb(62, 62, 62));
            AddPreset(presets, "浅色", Color.FromArgb(243, 243, 243), Color.FromArgb(24, 24, 24),
                Color.FromArgb(92, 92, 92), Color.FromArgb(198, 198, 198));
            AddPreset(presets, "蓝夜", Color.FromArgb(15, 26, 43), Color.FromArgb(232, 244, 255),
                Color.FromArgb(143, 184, 222), Color.FromArgb(39, 73, 109));
            AddPreset(presets, "紫夜", Color.FromArgb(27, 19, 37), Color.FromArgb(244, 235, 255),
                Color.FromArgb(185, 167, 208), Color.FromArgb(75, 58, 99));
            AddPreset(presets, "森林", Color.FromArgb(16, 32, 26), Color.FromArgb(232, 255, 244),
                Color.FromArgb(156, 207, 183), Color.FromArgb(44, 90, 69));
            AddPreset(presets, "暖灰", Color.FromArgb(42, 38, 34), Color.FromArgb(255, 246, 233),
                Color.FromArgb(205, 187, 160), Color.FromArgb(90, 74, 58));
            page.Controls.Add(presets);

            page.Controls.Add(Hint("自定义颜色（点击色块打开 Windows 颜色盘）"));

            _backgroundButton = MakeColorButton();
            _backgroundButton.Click += delegate { PickColor(0); };
            _textButton = MakeColorButton();
            _textButton.Click += delegate { PickColor(1); };
            _labelButton = MakeColorButton();
            _labelButton.Click += delegate { PickColor(2); };
            _borderButton = MakeColorButton();
            _borderButton.Click += delegate { PickColor(3); };

            TableLayoutPanel colors = new TableLayoutPanel();
            colors.ColumnCount = 2;
            colors.RowCount = 2;
            colors.AutoSize = true;
            colors.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            colors.Width = ContentWidth;
            colors.Margin = new Padding(0, 0, 0, 6);
            colors.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 258f));
            colors.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 258f));
            colors.Controls.Add(_backgroundButton, 0, 0);
            colors.Controls.Add(_textButton, 1, 0);
            colors.Controls.Add(_labelButton, 0, 1);
            colors.Controls.Add(_borderButton, 1, 1);
            page.Controls.Add(colors);

            AppendImageSection(page);
            AppendSaveRow(page);
            return page;
        }

        Control BuildContentPage()
        {
            FlowLayoutPanel page = NewPage();
            page.Controls.Add(Header("显示的指标"));

            Label desc = new Label();
            desc.Text = "勾选要显示的项目，并可以直接修改它在面板上的名称。"
                + "\n名称留空会恢复默认值；网络三项的默认名称显示为箭头图标。";
            desc.AutoSize = false;
            desc.Width = ContentWidth;
            desc.Height = 42;
            desc.ForeColor = HintColor;
            desc.Margin = new Padding(0, 0, 0, 10);
            page.Controls.Add(desc);

            FlowLayoutPanel head = new FlowLayoutPanel();
            head.FlowDirection = FlowDirection.LeftToRight;
            head.WrapContents = false;
            head.AutoSize = true;
            head.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            head.Margin = new Padding(0, 0, 0, 2);
            head.Controls.Add(HeadLabel("指标", 120));
            head.Controls.Add(HeadLabel("显示名称", 300));
            head.Controls.Add(HeadLabel("拖动排序", 90));
            page.Controls.Add(head);

            _orderList = new FlowLayoutPanel();
            _orderList.FlowDirection = FlowDirection.TopDown;
            _orderList.WrapContents = false;
            _orderList.AutoSize = true;
            _orderList.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _orderList.Width = ContentWidth;
            _orderList.Margin = new Padding(0, 0, 0, 8);
            page.Controls.Add(_orderList);

            string[] visibilityNames = { "CPU", "GPU", "内存", "磁盘", "上传", "下载", "上下行" };
            _visibilityChecks = new CheckBox[visibilityNames.Length];
            _nameBoxes = new TextBox[visibilityNames.Length];
            _rows = new Panel[visibilityNames.Length];
            for (int i = 0; i < visibilityNames.Length; i++)
            {
                _rows[i] = CreateMetricRow(i, visibilityNames[i]);
                _orderList.Controls.Add(_rows[i]);
            }
            ApplyOrderToList();

            FlowLayoutPanel nameButtons = new FlowLayoutPanel();
            nameButtons.FlowDirection = FlowDirection.LeftToRight;
            nameButtons.WrapContents = false;
            nameButtons.AutoSize = true;
            nameButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            nameButtons.Margin = new Padding(0, 0, 0, 8);
            Button resetNames = new Button();
            resetNames.Text = "恢复默认名称";
            resetNames.Width = 122;
            resetNames.Height = 30;
            resetNames.Margin = new Padding(0, 0, 8, 0);
            resetNames.Click += delegate
            {
                _draft.ResetMetricNames();
                MarkDirty();
                RefreshAll();
            };
            nameButtons.Controls.Add(resetNames);
            page.Controls.Add(nameButtons);

            page.Controls.Add(Hint("按住右侧握把上下拖动即可调整顺序。"));
            AppendSaveRow(page);
            return page;
        }

        Panel CreateMetricRow(int metric, string caption)
        {
            int bits = 1 << metric;
            Panel row = new Panel();
            row.Width = ContentWidth;
            row.Height = 32;
            row.Margin = new Padding(0, 0, 0, 2);
            row.Tag = metric;
            row.BackColor = BackColor;

            CheckBox box = new CheckBox();
            box.Text = caption;
            box.Tag = bits;
            box.AutoSize = false;
            box.Width = 114;
            box.Height = 26;
            box.Location = new Point(0, 3);
            box.CheckedChanged += delegate
            {
                if (_loading) return;
                int mask = _draft.VisibleMask;
                if (box.Checked) mask |= bits;
                else mask &= ~bits;
                if ((mask & 0x7F) == 0)
                {
                    _loading = true;
                    box.Checked = true;
                    _loading = false;
                    return;
                }
                _draft.VisibleMask = mask & 0x7F;
                MarkDirty();
            };
            _visibilityChecks[metric] = box;
            row.Controls.Add(box);

            TextBox name = new TextBox();
            name.Width = 290;
            name.MaxLength = 10;
            name.Location = new Point(118, 4);
            name.Text = _draft.MetricNames[metric];
            name.TextChanged += delegate
            {
                if (_loading) return;
                _draft.MetricNames[metric] = name.Text;
                MarkDirty();
            };
            name.Leave += delegate
            {
                string normalized = AppSettings.NormalizeName(name.Text, metric);
                if (!string.Equals(name.Text, normalized, StringComparison.Ordinal))
                    name.Text = normalized;
            };
            _nameBoxes[metric] = name;
            row.Controls.Add(name);

            GripHandle handle = new GripHandle();
            handle.Width = 44;
            handle.Height = 26;
            handle.Location = new Point(416, 3);
            handle.ForeColor = Color.FromArgb(120, 124, 130);
            handle.MouseDown += OnRowHandleMouseDown;
            handle.MouseMove += OnRowHandleMouseMove;
            handle.MouseUp += OnRowHandleMouseUp;
            row.Controls.Add(handle);

            return row;
        }

        void OnRowHandleMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _orderList == null) return;
            Control handle = (Control)sender;
            Panel row = handle.Parent as Panel;
            if (row == null) return;
            _dragRow = row;
            _dragStartY = Cursor.Position.Y;
            _dragStep = row.Height + row.Margin.Vertical;
            row.BackColor = Color.FromArgb(219, 234, 250);
            handle.Capture = true;
        }

        void OnRowHandleMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragRow == null || _orderList == null) return;
            int delta = Cursor.Position.Y - _dragStartY;
            if (Math.Abs(delta) < Math.Max(8, _dragStep)) return;

            if (StepRow(_dragRow, delta > 0 ? 1 : -1))
                _dragStartY += delta > 0 ? _dragStep : -_dragStep;
        }

        bool StepRow(Panel row, int direction)
        {
            if (_orderList == null || row == null) return false;
            int index = _orderList.Controls.GetChildIndex(row);
            int target = index + direction;
            if (index < 0 || target < 0 || target >= _orderList.Controls.Count) return false;
            _orderList.Controls.SetChildIndex(row, target);
            CommitOrder();
            return true;
        }

        void OnRowHandleMouseUp(object sender, MouseEventArgs e)
        {
            if (_dragRow == null) return;
            _dragRow.BackColor = BackColor;
            _dragRow = null;
            Control handle = sender as Control;
            if (handle != null) handle.Capture = false;
        }

        void CommitOrder()
        {
            if (_orderList == null) return;
            int[] order = new int[_orderList.Controls.Count];
            for (int i = 0; i < order.Length; i++)
                order[i] = (int)_orderList.Controls[i].Tag;
            _draft.MetricOrder = order;
            MarkDirty();
        }

        void ApplyOrderToList()
        {
            if (_orderList == null) return;
            int[] order = _draft.GetMetricOrder();
            for (int i = 0; i < order.Length; i++)
            {
                Panel row = FindRow(order[i]);
                if (row != null) _orderList.Controls.SetChildIndex(row, i);
            }
        }

        Panel FindRow(int metric)
        {
            if (_rows == null || metric < 0 || metric >= _rows.Length) return null;
            return _rows[metric];
        }

        static Label HeadLabel(string text, int width)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Width = width;
            label.Height = 22;
            label.ForeColor = HintColor;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Margin = new Padding(0);
            return label;
        }

        void AppendImageSection(FlowLayoutPanel page)
        {
            page.Controls.Add(Header("背景图片"));

            FlowLayoutPanel imageRow = new FlowLayoutPanel();
            imageRow.FlowDirection = FlowDirection.LeftToRight;
            imageRow.WrapContents = false;
            imageRow.AutoSize = true;
            imageRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            imageRow.Width = ContentWidth;
            imageRow.Margin = new Padding(0, 0, 0, 8);
            Button chooseImage = new Button();
            chooseImage.Text = "选择图片...";
            chooseImage.Width = 108;
            chooseImage.Height = 30;
            chooseImage.Margin = new Padding(0, 0, 8, 0);
            chooseImage.Click += delegate { ChooseImage(); };
            imageRow.Controls.Add(chooseImage);
            Button clearImage = new Button();
            clearImage.Text = "清除图片";
            clearImage.Width = 88;
            clearImage.Height = 30;
            clearImage.Margin = new Padding(0, 0, 8, 0);
            clearImage.Click += delegate { ClearImage(); };
            imageRow.Controls.Add(clearImage);
            _imagePathLabel = new Label();
            _imagePathLabel.AutoSize = false;
            _imagePathLabel.Width = 290;
            _imagePathLabel.Height = 26;
            _imagePathLabel.AutoEllipsis = true;
            _imagePathLabel.TextAlign = ContentAlignment.MiddleLeft;
            _imagePathLabel.Margin = new Padding(0, 3, 0, 0);
            imageRow.Controls.Add(_imagePathLabel);
            page.Controls.Add(imageRow);

            FlowLayoutPanel imageOptions = new FlowLayoutPanel();
            imageOptions.FlowDirection = FlowDirection.LeftToRight;
            imageOptions.WrapContents = false;
            imageOptions.AutoSize = true;
            imageOptions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            imageOptions.Width = ContentWidth;
            imageOptions.Margin = new Padding(0, 0, 0, 6);
            Label fitLabel = new Label();
            fitLabel.Text = "填充方式";
            fitLabel.AutoSize = true;
            fitLabel.Margin = new Padding(0, 9, 8, 0);
            imageOptions.Controls.Add(fitLabel);
            _imageFitCombo = new ComboBox();
            _imageFitCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _imageFitCombo.Width = 110;
            _imageFitCombo.Margin = new Padding(0, 4, 16, 0);
            _imageFitCombo.Items.AddRange(new object[] { "填充裁剪", "拉伸", "完整显示", "平铺" });
            _imageFitCombo.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                _draft.BackgroundImageFit = _imageFitCombo.SelectedIndex;
                MarkDirty();
            };
            imageOptions.Controls.Add(_imageFitCombo);
            Label dimLabel = new Label();
            dimLabel.Text = "遮罩";
            dimLabel.AutoSize = true;
            dimLabel.Margin = new Padding(0, 9, 6, 0);
            imageOptions.Controls.Add(dimLabel);
            _imageDimSlider = new TrackBar();
            _imageDimSlider.Minimum = 0;
            _imageDimSlider.Maximum = 80;
            _imageDimSlider.TickFrequency = 10;
            _imageDimSlider.SmallChange = 5;
            _imageDimSlider.LargeChange = 10;
            _imageDimSlider.Width = 180;
            _imageDimSlider.Height = 30;
            _imageDimSlider.Margin = new Padding(0, 0, 0, 0);
            _imageDimSlider.ValueChanged += delegate
            {
                if (_loading) return;
                _draft.BackgroundImageDim = _imageDimSlider.Value;
                _imageDimValue.Text = _imageDimSlider.Value + "%";
                MarkDirty();
            };
            imageOptions.Controls.Add(_imageDimSlider);
            _imageDimValue = new Label();
            _imageDimValue.AutoSize = false;
            _imageDimValue.Width = 48;
            _imageDimValue.Height = 24;
            _imageDimValue.TextAlign = ContentAlignment.MiddleLeft;
            _imageDimValue.Margin = new Padding(6, 8, 0, 0);
            imageOptions.Controls.Add(_imageDimValue);
            page.Controls.Add(imageOptions);

            page.Controls.Add(Hint("遮罩越深，图片上的文字越好认。选择图片会自动关闭透明样式。"));
        }

        Control BuildLayoutPage()
        {
            FlowLayoutPanel page = NewPage();
            page.Controls.Add(Header("数值对齐"));

            FlowLayoutPanel alignRow = new FlowLayoutPanel();
            alignRow.FlowDirection = FlowDirection.LeftToRight;
            alignRow.WrapContents = false;
            alignRow.AutoSize = true;
            alignRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            alignRow.Margin = new Padding(0, 0, 0, 6);
            _alignCenter = new RadioButton();
            _alignCenter.Text = "居中";
            _alignCenter.AutoSize = true;
            _alignCenter.Margin = new Padding(0, 0, 20, 0);
            _alignCenter.CheckedChanged += delegate
            {
                if (_loading || !_alignCenter.Checked) return;
                _draft.ValueAlign = 0;
                MarkDirty();
            };
            _alignRight = new RadioButton();
            _alignRight.Text = "向右对齐";
            _alignRight.AutoSize = true;
            _alignRight.CheckedChanged += delegate
            {
                if (_loading || !_alignRight.Checked) return;
                _draft.ValueAlign = 1;
                MarkDirty();
            };
            alignRow.Controls.Add(_alignCenter);
            alignRow.Controls.Add(_alignRight);
            page.Controls.Add(alignRow);
            page.Controls.Add(Hint("列名固定在格子左侧，数值在列名右侧的空间里居中或贴右。"));

            page.Controls.Add(Header("列间距"));

            _columnGapSlider = new TrackBar();
            _columnGapSlider.Minimum = 0;
            _columnGapSlider.Maximum = 32;
            _columnGapSlider.TickFrequency = 4;
            _columnGapSlider.SmallChange = 1;
            _columnGapSlider.LargeChange = 4;
            _columnGapSlider.Width = 260;
            _columnGapSlider.Height = 30;
            _columnGapSlider.ValueChanged += delegate
            {
                if (_loading) return;
                _draft.ColumnGap = _columnGapSlider.Value;
                _columnGapValue.Text = _columnGapSlider.Value + " px";
                MarkDirty();
            };
            _columnGapValue = new Label();
            _columnGapValue.AutoSize = false;
            _columnGapValue.Width = 56;
            _columnGapValue.Height = 24;
            _columnGapValue.TextAlign = ContentAlignment.MiddleLeft;
            _columnGapValue.Text = "0 px";
            page.Controls.Add(SliderRow("列与列之间间距", _columnGapSlider, _columnGapValue));

            page.Controls.Add(Hint("加宽列间距会同时加宽面板。"));
            AppendSaveRow(page);
            return page;
        }

        Control SliderRow(string title, TrackBar slider, Label value)
        {
            FlowLayoutPanel row = new FlowLayoutPanel();
            row.FlowDirection = FlowDirection.LeftToRight;
            row.WrapContents = false;
            row.AutoSize = true;
            row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            row.Width = ContentWidth;
            row.Margin = new Padding(0, 0, 0, 6);
            Label label = new Label();
            label.Text = title;
            label.AutoSize = false;
            label.Width = 132;
            label.Height = 24;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Margin = new Padding(0, 6, 6, 0);
            row.Controls.Add(label);
            slider.Margin = new Padding(0, 0, 0, 0);
            row.Controls.Add(slider);
            value.Margin = new Padding(8, 8, 0, 0);
            row.Controls.Add(value);
            return row;
        }

        // 换位置模式时先解锁，方便用户立刻拖动面板。
        void ApplyMode(DisplayMode mode)
        {
            if (_draft.Mode == mode) return;
            _draft.Mode = mode;
            _draft.Locked = false;
            MarkDirty();
            _loading = true;
            _lockCheck.Checked = false;
            _loading = false;
        }

        Control BuildPositionPage()
        {
            FlowLayoutPanel page = NewPage();
            page.Controls.Add(Header("位置模式"));

            FlowLayoutPanel modeRow = new FlowLayoutPanel();
            modeRow.FlowDirection = FlowDirection.LeftToRight;
            modeRow.WrapContents = false;
            modeRow.AutoSize = true;
            modeRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            modeRow.Margin = new Padding(0, 0, 0, 8);
            _taskbarMode = new RadioButton();
            _taskbarMode.Text = "任务栏模式";
            _taskbarMode.AutoSize = true;
            _taskbarMode.Margin = new Padding(0, 0, 20, 0);
            _taskbarMode.CheckedChanged += delegate
            {
                if (_loading || !_taskbarMode.Checked) return;
                ApplyMode(DisplayMode.Taskbar);
            };
            _desktopMode = new RadioButton();
            _desktopMode.Text = "屏幕浮窗模式";
            _desktopMode.AutoSize = true;
            _desktopMode.CheckedChanged += delegate
            {
                if (_loading || !_desktopMode.Checked) return;
                ApplyMode(DisplayMode.Desktop);
            };
            modeRow.Controls.Add(_taskbarMode);
            modeRow.Controls.Add(_desktopMode);
            page.Controls.Add(modeRow);

            FlowLayoutPanel layerRow = new FlowLayoutPanel();
            layerRow.FlowDirection = FlowDirection.LeftToRight;
            layerRow.WrapContents = false;
            layerRow.AutoSize = true;
            layerRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            layerRow.Margin = new Padding(0, 0, 0, 8);
            Label layerTitle = new Label();
            layerTitle.Text = "页面层叠：";
            layerTitle.AutoSize = true;
            layerTitle.Margin = new Padding(0, 6, 6, 0);
            layerRow.Controls.Add(layerTitle);
            _desktopTopRadio = new RadioButton();
            _desktopTopRadio.Text = "置顶";
            _desktopTopRadio.AutoSize = true;
            _desktopTopRadio.Margin = new Padding(0, 0, 20, 0);
            _desktopTopRadio.CheckedChanged += delegate
            {
                if (_loading || !_desktopTopRadio.Checked) return;
                _draft.DesktopTopMost = true;
                MarkDirty();
            };
            layerRow.Controls.Add(_desktopTopRadio);
            _desktopBottomRadio = new RadioButton();
            _desktopBottomRadio.Text = "置底";
            _desktopBottomRadio.AutoSize = true;
            _desktopBottomRadio.CheckedChanged += delegate
            {
                if (_loading || !_desktopBottomRadio.Checked) return;
                _draft.DesktopTopMost = false;
                MarkDirty();
            };
            layerRow.Controls.Add(_desktopBottomRadio);
            page.Controls.Add(layerRow);

            _lockCheck = new CheckBox();
            _lockCheck.Text = "固定位置（锁定后不能拖动，避免误触）";
            _lockCheck.AutoSize = true;
            _lockCheck.Margin = new Padding(0, 0, 0, 8);
            _lockCheck.CheckedChanged += delegate
            {
                if (_loading) return;
                _draft.Locked = _lockCheck.Checked;
                MarkDirty();
            };
            page.Controls.Add(_lockCheck);

            FlowLayoutPanel positionButtons = new FlowLayoutPanel();
            positionButtons.FlowDirection = FlowDirection.LeftToRight;
            positionButtons.WrapContents = false;
            positionButtons.AutoSize = true;
            positionButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            positionButtons.Margin = new Padding(0, 0, 0, 10);
            Button unlock = new Button();
            unlock.Text = "解锁并拖动";
            unlock.Width = 112;
            unlock.Height = 30;
            unlock.Margin = new Padding(0, 0, 8, 0);
            unlock.Click += delegate
            {
                _draft.Locked = false;
                _loading = true;
                _lockCheck.Checked = false;
                _loading = false;
                ApplyDraft();
                MessageBox.Show("已解锁并保存。\n\n按住面板左键拖动即可移动；\n移动完成后可在右键菜单中重新固定。",
                    "任务栏性能", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            };
            positionButtons.Controls.Add(unlock);
            Button resetPos = new Button();
            resetPos.Text = "重置位置";
            resetPos.Width = 96;
            resetPos.Height = 30;
            resetPos.Click += delegate
            {
                _resetPosition();
                RefreshAll();
            };
            positionButtons.Controls.Add(resetPos);
            page.Controls.Add(positionButtons);

            page.Controls.Add(Hint("任务栏模式：拖动只改变水平位置，并跟随任务栏显示/隐藏。"
                + "\n屏幕浮窗模式：可在屏幕工作区拖动，不会进入任务栏。"));
            AppendSaveRow(page);
            return page;
        }

        Control BuildAboutPage()
        {
            FlowLayoutPanel page = NewPage();
            page.Controls.Add(Header("关于任务栏性能监控 2.0"));

            Label intro = new Label();
            intro.Text = "在任务栏或屏幕任意位置显示 CPU、GPU、内存、磁盘和网络速率。"
                + "\n数据来源：Windows 性能计数器、网络接口统计、GlobalMemoryStatusEx。";
            intro.AutoSize = false;
            intro.Width = ContentWidth;
            intro.Height = 62;
            intro.ForeColor = HintColor;
            intro.Margin = new Padding(0, 0, 0, 10);
            page.Controls.Add(intro);

            // 说明来源：文字会长到自动折行，用 MaximumSize 让它自己算高度，避免被裁掉。
            Label origin = new Label();
            origin.Text = "本项目是学习 AI 编程过程中的练习产物：需求由我提出，代码由 AI 辅助生成。\n"
                + "界面与产品思路借鉴自 Traffic Monitor（作者 zhongyang219，GPL-3.0），未复用其任何源码。\n"
                + "日常使用建议直接用 Traffic Monitor，本项目只适合作学习参考。";
            origin.AutoSize = true;
            origin.MaximumSize = new Size(ContentWidth, 0);
            origin.ForeColor = HintColor;
            origin.Margin = new Padding(0, 0, 0, 12);
            page.Controls.Add(origin);

            _aboutLabel = new Label();
            _aboutLabel.AutoSize = false;
            _aboutLabel.Width = ContentWidth;
            _aboutLabel.Height = 210;
            _aboutLabel.Margin = new Padding(0, 0, 0, 10);
            page.Controls.Add(_aboutLabel);

            page.Controls.Add(Hint("快捷键：Ctrl+Alt+S 打开设置；右键面板可快速切换常用项。"));

            page.Controls.Add(Hint("恢复默认设置、开机自启动、配置文件位置都在“基础设置”里。"));
            return page;
        }

        void RefreshAboutText()
        {
            if (_aboutLabel == null) return;
            StringBuilder sb = new StringBuilder();
            MetricsSnapshot snapshot = _snapshotProvider != null ? _snapshotProvider() : null;
            sb.AppendLine(WidgetForm.BuildDetailsText(snapshot));
            sb.AppendLine();

            int[] order = _draft.GetMetricOrder();
            StringBuilder shown = new StringBuilder();
            for (int i = 0; i < order.Length; i++)
            {
                int metric = order[i];
                if ((_draft.VisibleMask & (1 << metric)) == 0) continue;
                if (shown.Length > 0) shown.Append("、");
                shown.Append(_draft.GetMetricName(metric));
            }
            sb.AppendLine("显示内容：" + (shown.Length == 0 ? "无" : shown.ToString()));
            sb.AppendLine("外观：" + (_draft.TransparentStyle ? "透明样式（仅文字）" : "卡片样式")
                + "，" + (_draft.FollowSystem ? "跟随系统颜色" : "自定义配色"));
            sb.AppendLine("布局：数值" + (_draft.ValueAlign == 1 ? "向右对齐" : "居中")
                + "，列间距 " + _draft.ColumnGap + " px");
            sb.AppendLine("位置：" + (_draft.Mode == DisplayMode.Taskbar
                ? "任务栏"
                : "屏幕浮窗（" + (_draft.DesktopTopMost ? "置顶" : "置底") + "）")
                + "，" + (_draft.Locked ? "已固定" : "可拖动"));
            sb.Append("刷新间隔：" + _draft.RefreshMs + " ms");
            sb.AppendLine();
            sb.Append("配置文件：settings.ini（目录可在“基础设置”里改）");
            _aboutLabel.Text = sb.ToString();
        }

        // ---------------------------------------------------------------- helpers

        static Label Header(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
            label.Margin = new Padding(0, 4, 0, 8);
            return label;
        }

        static Label Hint(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Width = ContentWidth;
            int lines = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') lines++;
            label.Height = lines > 1 ? 26 + 20 * lines : 24;
            label.ForeColor = HintColor;
            label.Margin = new Padding(0, 6, 0, 8);
            return label;
        }

        Button MakeColorButton()
        {
            Button button = new Button();
            button.Width = 250;
            button.Height = 40;
            button.Margin = new Padding(0, 0, 8, 8);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(150, 150, 150);
            return button;
        }

        void AddPreset(FlowLayoutPanel panel, string name, Color background, Color text,
            Color label, Color border)
        {
            Button button = new Button();
            button.Text = name;
            button.Width = 76;
            button.Height = 30;
            button.Margin = new Padding(0, 0, 6, 6);
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = background;
            button.ForeColor = ThemeService.ReadableTextFor(background);
            button.FlatAppearance.BorderColor = border;
            button.Cursor = Cursors.Hand;
            button.Click += delegate
            {
                _draft.FollowSystem = false;
                _draft.Background = background;
                _draft.TextColor = text;
                _draft.LabelColor = label;
                _draft.BorderColor = border;
                MarkDirty();
                RefreshAll();
            };
            panel.Controls.Add(button);
        }

        // ---------------------------------------------------------------- state

        void RefreshAll()
        {
            _loading = true;
            _follow.Checked = _draft.FollowSystem;
            if (_autoStartCheck != null) _autoStartCheck.Checked = AutoStart.IsEnabled();
            if (_dirLabel != null) _dirLabel.Text = _draft.DataDirectory;
            _transparent.Checked = _draft.TransparentStyle;
            _alignCenter.Checked = _draft.ValueAlign == 0;
            _alignRight.Checked = _draft.ValueAlign == 1;
            _columnGapSlider.Value = Math.Max(_columnGapSlider.Minimum, Math.Min(_columnGapSlider.Maximum, _draft.ColumnGap));
            _columnGapValue.Text = _columnGapSlider.Value + " px";
            _taskbarMode.Checked = _draft.Mode == DisplayMode.Taskbar;
            _desktopMode.Checked = _draft.Mode == DisplayMode.Desktop;
            _desktopTopRadio.Checked = _draft.DesktopTopMost;
            _desktopBottomRadio.Checked = !_draft.DesktopTopMost;
            _lockCheck.Checked = _draft.Locked;
            for (int i = 0; i < _visibilityChecks.Length; i++)
            {
                _visibilityChecks[i].Checked =
                    (_draft.VisibleMask & (int)_visibilityChecks[i].Tag) != 0;
                if (_nameBoxes != null && _nameBoxes[i] != null)
                    _nameBoxes[i].Text = _draft.MetricNames[i];
            }
            ApplyOrderToList();
            _imagePathLabel.Text = string.IsNullOrEmpty(_draft.BackgroundImagePath)
                ? "未选择图片"
                : Path.GetFileName(_draft.BackgroundImagePath);
            _imageFitCombo.SelectedIndex = Math.Max(0, Math.Min(3, _draft.BackgroundImageFit));
            _imageDimSlider.Value = Math.Max(_imageDimSlider.Minimum,
                Math.Min(_imageDimSlider.Maximum, _draft.BackgroundImageDim));
            _imageDimValue.Text = _imageDimSlider.Value + "%";
            _loading = false;
            RefreshColorButtons();
            UpdateColorControlState();
            RefreshAboutText();
        }

        void UpdateColorControlState()
        {
            _backgroundButton.Enabled = !_draft.TransparentStyle;
            _borderButton.Enabled = !_draft.TransparentStyle;
        }

        void RefreshColorButtons()
        {
            Palette palette = ThemeService.Create(_draft);
            SetColorButton(_backgroundButton, "背景", palette.Background);
            SetColorButton(_textButton, "文字", palette.Text);
            SetColorButton(_labelButton, "标签", palette.Label);
            SetColorButton(_borderButton, "边框", palette.Border);
        }

        static void SetColorButton(Button button, string name, Color color)
        {
            button.BackColor = color;
            button.ForeColor = ThemeService.ReadableTextFor(color);
            button.Text = name + "    " + ToHex(color);
        }

        static string ToHex(Color color)
        {
            return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        void ChooseImage()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择背景图片";
                dialog.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    _draft.BackgroundImagePath = BackgroundImageStore.Import(dialog.FileName);
                    _draft.TransparentStyle = false;
                    MarkDirty();
                    RefreshAll();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Importing background image failed: " + dialog.FileName, ex);
                    MessageBox.Show("加载背景图片失败：\n" + ex.Message, "任务栏性能",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        void ClearImage()
        {
            // 图片文件在保存时才清理，取消编辑不会影响当前背景。
            _draft.BackgroundImagePath = "";
            MarkDirty();
            RefreshAll();
        }

        void PickColor(int target)
        {
            Palette palette = ThemeService.Create(_draft);
            Color current;
            if (target == 0) current = palette.Background;
            else if (target == 1) current = palette.Text;
            else if (target == 2) current = palette.Label;
            else current = palette.Border;

            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = current;
                dialog.FullOpen = true;
                dialog.AnyColor = true;
                dialog.CustomColors = new int[] { current.ToArgb() };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _draft.FollowSystem = false;
                    if (target == 0) _draft.Background = dialog.Color;
                    else if (target == 1) _draft.TextColor = dialog.Color;
                    else if (target == 2) _draft.LabelColor = dialog.Color;
                    else _draft.BorderColor = dialog.Color;
                    MarkDirty();
                    RefreshAll();
                }
            }
        }

        // 改动立即作用到面板上做预览，但不写注册表；点“保存”才落盘。
        void MarkDirty()
        {
            _dirty = true;
            UpdateSaveButtons();
            PreviewDraft();
        }

        void PreviewDraft()
        {
            if (_live == null) return;
            _live.AutoSave = false;
            _live.CopyFrom(_draft);
            if (_changed != null) _changed();
        }

        // 把草稿写回正在运行的配置并写入配置文件。
        void ApplyDraft()
        {
            _live.CopyFrom(_draft);
            _live.AutoSave = true;
            _live.Save();
            AppLog.SetDirectory(_live.DataDirectory);
            ApplyAutoStart();
            _saved = _draft.Clone();
            _dirty = false;
            UpdateSaveButtons();
            BackgroundImageStore.RemoveOrphans(_live.BackgroundImagePath);
            if (_changed != null) _changed();
        }

        // 开机自启动写入注册表启动项，只在保存时执行。
        void ApplyAutoStart()
        {
            if (_autoStartCheck == null) return;
            try
            {
                if (AutoStart.IsEnabled() != _autoStartCheck.Checked)
                    AutoStart.SetEnabled(_autoStartCheck.Checked);
            }
            catch (Exception ex)
            {
                AppLog.Error("Auto start change failed", ex);
                MessageBox.Show("无法修改开机自启动：\n" + ex.Message, "任务栏性能",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 放弃本次改动，回到上次保存时的状态。
        void RevertPreview()
        {
            if (_saved != null) _live.CopyFrom(_saved);
            _live.AutoSave = true;
            _dirty = false;
            UpdateSaveButtons();
            if (_autoStartCheck != null)
            {
                _loading = true;
                _autoStartCheck.Checked = AutoStart.IsEnabled();
                _dirLabel.Text = _live.DataDirectory;
                _loading = false;
            }
            if (_changed != null) _changed();
        }

        void UpdateSaveButtons()
        {
            for (int i = 0; i < _saveButtons.Count; i++)
                _saveButtons[i].Enabled = _dirty;
        }

        // 每个分类页底部各放一个“保存”，改动只在本页保存后才生效。
        void AppendSaveRow(FlowLayoutPanel page)
        {
            FlowLayoutPanel row = new FlowLayoutPanel();
            row.FlowDirection = FlowDirection.RightToLeft;
            row.WrapContents = false;
            row.AutoSize = false;
            row.Width = ContentWidth;
            row.Height = 38;
            row.Margin = new Padding(0, 10, 0, 2);

            Button save = new Button();
            save.Text = "保存";
            save.Width = 96;
            save.Height = 30;
            save.FlatStyle = FlatStyle.System;
            save.Enabled = false;
            save.Click += delegate { ApplyDraft(); };
            _saveButtons.Add(save);
            row.Controls.Add(save);

            page.Controls.Add(row);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty)
            {
                DialogResult answer = MessageBox.Show(
                    "有未保存的修改。\n\n保存：写入并保留这些改动\n不保存：还原到上次保存时的样子",
                    "设置", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (answer == DialogResult.Yes) ApplyDraft();
                else RevertPreview();
            }
            base.OnFormClosing(e);
        }
    }
}
