using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarMetrics
{
    enum DisplayMode
    {
        Taskbar = 0,
        Desktop = 1
    }

    sealed class AppSettings
    {
        const string KeyPath = @"Software\TaskbarMetrics";
        const string FileName = "settings.ini";

        public int RefreshMs = 1000;
        public bool FollowSystem = true;
        public bool TransparentStyle = false;

        public Color Background = Color.FromArgb(31, 31, 31);
        public Color TextColor = Color.FromArgb(242, 242, 242);
        public Color LabelColor = Color.FromArgb(162, 162, 162);
        public Color BorderColor = Color.FromArgb(62, 62, 62);

        public int ValueAlign = 0; // 0 数值居中，1 数值向右对齐
        public int ColumnGap = 0;

        // 0x01 CPU | 0x02 GPU | 0x04 内存 | 0x08 磁盘
        // 0x10 上传 | 0x20 下载 | 0x40 上下行（同一格内两行，小字号）
        public int VisibleMask = 0x3F;

        // 每个指标在面板上显示的名称，留空则用默认值。
        // 网络的默认名称是箭头，绘制时用矢量图标而不是文字。
        public static readonly string[] DefaultMetricNames = { "CPU", "GPU", "内存", "磁盘", "↑", "↓", "↑↓" };
        public string[] MetricNames = (string[])DefaultMetricNames.Clone();

        // 面板上的显示顺序（指标索引），行优先排列。
        public static readonly int[] DefaultMetricOrder = { 0, 1, 2, 3, 4, 5, 6 };
        public int[] MetricOrder = (int[])DefaultMetricOrder.Clone();

        public string BackgroundImagePath = "";
        public int BackgroundImageFit = 0; // 0 cover, 1 stretch, 2 contain, 3 tile
        public int BackgroundImageDim = 35;

        public DisplayMode Mode = DisplayMode.Taskbar;
        public bool Locked = true;
        public bool DesktopTopMost = true;

        // 配置文件所在目录，可以在“基础设置”里改成任意位置。
        public string DataDirectory = "";

        // 设置窗口实时预览时临时关掉写盘，点“保存”时才真正落盘。
        public bool AutoSave = true;

        public bool HasTaskbarX = false;
        public int TaskbarX = 0;

        public int DesktopX = -1;
        public int DesktopY = -1;

        delegate int IntReader(string name, int fallback);
        delegate string TextReader(string name, string fallback);

        public string SettingsFile
        {
            get
            {
                string dir = string.IsNullOrEmpty(DataDirectory) ? DefaultDirectory() : DataDirectory;
                return Path.Combine(dir, FileName);
            }
        }

        public static string DefaultDirectory()
        {
            try
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TaskbarMetrics");
            }
            catch
            {
                return Path.GetTempPath();
            }
        }

        // ---------------------------------------------------------------- load / save

        public static AppSettings Load()
        {
            return Load(null);
        }

        // directory 为空时按注册表里的指针找配置文件，否则直接用指定目录。
        public static AppSettings Load(string directory)
        {
            AppSettings s = new AppSettings();
            s.DataDirectory = string.IsNullOrEmpty(directory) ? ReadDataDirectory() : directory;

            // 读不到配置文件时，默认在程序所在目录生成一份。
            if (!File.Exists(Path.Combine(s.DataDirectory, FileName)))
            {
                string programDir = ProgramDirectory();
                if (!string.Equals(programDir, s.DataDirectory, StringComparison.OrdinalIgnoreCase))
                    s.DataDirectory = programDir;
            }

            if (!EnsureDirectory(s))
            {
                s.DataDirectory = DefaultDirectory();
                EnsureDirectory(s);
            }

            // 日志（含报错）跟着配置目录走，所以在读配置之前就切过去。
            AppLog.SetDirectory(s.DataDirectory);

            bool fromFile = false;
            try
            {
                if (File.Exists(s.SettingsFile))
                {
                    Dictionary<string, string> values = ReadIni(s.SettingsFile);
                    s.ApplyValues(
                        delegate(string name, int fallback)
                        {
                            string text;
                            int number;
                            return values.TryGetValue(name, out text) && int.TryParse(text, out number)
                                ? number : fallback;
                        },
                        delegate(string name, string fallback)
                        {
                            string text;
                            return values.TryGetValue(name, out text) ? text : fallback;
                        });
                    fromFile = true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Reading settings file failed", ex);
            }

            if (!fromFile)
            {
                // 没有配置文件：先用旧注册表里的配置（升级场景），再立即写成文件。
                s.LoadFromRegistry();
                try { s.Save(); }
                catch { }
            }

            // 配置文件已经是唯一数据源，注册表里的旧键不再需要。
            ClearLegacyRegistryValues();

            return s;
        }

        public void Save()
        {
            if (!AutoSave) return; // 设置窗口预览期间不落盘
            try
            {
                Directory.CreateDirectory(DataDirectory);
                WriteSettingsFile();
                WriteRegistryPointer();
            }
            catch (Exception ex)
            {
                AppLog.Error("Saving settings failed", ex);
            }
        }

        static bool EnsureDirectory(AppSettings s)
        {
            try
            {
                Directory.CreateDirectory(s.DataDirectory);
                string probe = Path.Combine(s.DataDirectory, ".write-probe.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 程序所在目录（安装版就是 %LOCALAPPDATA%\TaskbarMetrics）。
        public static string ProgramDirectory()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            catch
            {
            }
            return DefaultDirectory();
        }

        // 迁移到配置文件后清掉旧键，避免以后文件丢失又读到过期值。
        static void ClearLegacyRegistryValues()
        {
            string[] names =
            {
                "RefreshMs", "FollowSystem", "TransparentStyle", "Background", "TextColor", "LabelColor",
                "BorderColor", "ValueAlign", "LabelAlign", "ColumnGap", "VisibleMask", "SettingsVersion",
                "Name0", "Name1", "Name2", "Name3", "Name4", "Name5", "Name6", "Order",
                "BackgroundImagePath", "BackgroundImageFit", "BackgroundImageDim", "Mode", "Locked",
                "DesktopTopMost", "HasTaskbarX", "TaskbarX", "DesktopX", "DesktopY", "NetworkDisplayMode",
                "LabelGap", "Columns", "CombineNetwork"
            };
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (key == null) return;
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (key.GetValue(names[i]) != null) key.DeleteValue(names[i], false);
                    }
                }
            }
            catch
            {
            }
        }

        static string ReadDataDirectory()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key != null)
                    {
                        string dir = key.GetValue("DataDirectory") as string;
                        if (!string.IsNullOrEmpty(dir)) return dir;
                    }
                }
            }
            catch
            {
            }
            return DefaultDirectory();
        }

        void LoadFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key == null) return;
                    ApplyValues(
                        delegate(string name, int fallback)
                        {
                            object value = key.GetValue(name);
                            return value is int ? (int)value : fallback;
                        },
                        delegate(string name, string fallback)
                        {
                            object value = key.GetValue(name);
                            return value is string ? (string)value : fallback;
                        });
                }
            }
            catch
            {
            }
        }

        void ApplyValues(IntReader readInt, TextReader readText)
        {
            RefreshMs = Clamp(readInt("RefreshMs", RefreshMs), 500, 10000);
            FollowSystem = readInt("FollowSystem", FollowSystem ? 1 : 0) != 0;
            TransparentStyle = readInt("TransparentStyle", TransparentStyle ? 1 : 0) != 0;
            Background = Color.FromArgb(readInt("Background", Background.ToArgb()));
            TextColor = Color.FromArgb(readInt("TextColor", TextColor.ToArgb()));
            LabelColor = Color.FromArgb(readInt("LabelColor", LabelColor.ToArgb()));
            BorderColor = Color.FromArgb(readInt("BorderColor", BorderColor.ToArgb()));
            ValueAlign = Clamp(readInt("ValueAlign", readInt("LabelAlign", ValueAlign)), 0, 1);
            ColumnGap = Clamp(readInt("ColumnGap", ColumnGap), 0, 32);
            VisibleMask = MigrateVisibleMask(readInt("VisibleMask", VisibleMask) & 0x7F,
                readInt("SettingsVersion", 1), readInt("NetworkDisplayMode", 2));
            for (int i = 0; i < MetricNames.Length; i++)
                MetricNames[i] = NormalizeName(readText("Name" + i, MetricNames[i]), i);
            MetricOrder = ParseOrder(readText("Order", ""));
            BackgroundImagePath = readText("BackgroundImagePath", BackgroundImagePath);
            BackgroundImageFit = Clamp(readInt("BackgroundImageFit", BackgroundImageFit), 0, 3);
            BackgroundImageDim = Clamp(readInt("BackgroundImageDim", BackgroundImageDim), 0, 80);
            Mode = readInt("Mode", (int)Mode) == 1 ? DisplayMode.Desktop : DisplayMode.Taskbar;
            Locked = readInt("Locked", Locked ? 1 : 0) != 0;
            DesktopTopMost = readInt("DesktopTopMost", DesktopTopMost ? 1 : 0) != 0;
            HasTaskbarX = readInt("HasTaskbarX", 0) != 0;
            TaskbarX = readInt("TaskbarX", 0);
            DesktopX = readInt("DesktopX", -1);
            DesktopY = readInt("DesktopY", -1);
        }

        void WriteSettingsFile()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 任务栏性能监控（TaskbarMetrics）配置文件");
            sb.AppendLine("# 可以直接编辑，程序会在下次启动时读取。");
            sb.AppendLine("Version=3");
            sb.AppendLine("RefreshMs=" + RefreshMs);
            sb.AppendLine("FollowSystem=" + (FollowSystem ? 1 : 0));
            sb.AppendLine("TransparentStyle=" + (TransparentStyle ? 1 : 0));
            sb.AppendLine("Background=" + Background.ToArgb());
            sb.AppendLine("TextColor=" + TextColor.ToArgb());
            sb.AppendLine("LabelColor=" + LabelColor.ToArgb());
            sb.AppendLine("BorderColor=" + BorderColor.ToArgb());
            sb.AppendLine("ValueAlign=" + ValueAlign);
            sb.AppendLine("ColumnGap=" + ColumnGap);
            sb.AppendLine("VisibleMask=" + VisibleMask);
            for (int i = 0; i < MetricNames.Length; i++)
                sb.AppendLine("Name" + i + "=" + (MetricNames[i] ?? ""));
            sb.AppendLine("Order=" + JoinOrder(GetMetricOrder()));
            sb.AppendLine("BackgroundImagePath=" + (BackgroundImagePath ?? ""));
            sb.AppendLine("BackgroundImageFit=" + BackgroundImageFit);
            sb.AppendLine("BackgroundImageDim=" + BackgroundImageDim);
            sb.AppendLine("Mode=" + (int)Mode);
            sb.AppendLine("Locked=" + (Locked ? 1 : 0));
            sb.AppendLine("DesktopTopMost=" + (DesktopTopMost ? 1 : 0));
            sb.AppendLine("HasTaskbarX=" + (HasTaskbarX ? 1 : 0));
            sb.AppendLine("TaskbarX=" + TaskbarX);
            sb.AppendLine("DesktopX=" + DesktopX);
            sb.AppendLine("DesktopY=" + DesktopY);
            File.WriteAllText(SettingsFile, sb.ToString(), new UTF8Encoding(false));
        }

        // 注册表里只留一个指向配置文件目录的指针。
        void WriteRegistryPointer()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (key == null) return;
                key.SetValue("DataDirectory", DataDirectory ?? "", RegistryValueKind.String);
            }
        }

        static Dictionary<string, string> ReadIni(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
            }
            return values;
        }

        // ---------------------------------------------------------------- copies

        public AppSettings Clone()
        {
            AppSettings copy = (AppSettings)MemberwiseClone();
            copy.MetricNames = (string[])MetricNames.Clone();
            copy.MetricOrder = (int[])GetMetricOrder().Clone();
            return copy;
        }

        // 设置窗口点“保存”时把草稿写回正在运行的这份配置。
        // 面板坐标不覆盖，避免拖动中的位置被旧值顶回去。
        public void CopyFrom(AppSettings source)
        {
            if (source == null) return;
            RefreshMs = source.RefreshMs;
            FollowSystem = source.FollowSystem;
            TransparentStyle = source.TransparentStyle;
            Background = source.Background;
            TextColor = source.TextColor;
            LabelColor = source.LabelColor;
            BorderColor = source.BorderColor;
            ValueAlign = source.ValueAlign;
            ColumnGap = source.ColumnGap;
            VisibleMask = source.VisibleMask;
            MetricNames = (string[])source.MetricNames.Clone();
            MetricOrder = (int[])source.GetMetricOrder().Clone();
            BackgroundImagePath = source.BackgroundImagePath;
            BackgroundImageFit = source.BackgroundImageFit;
            BackgroundImageDim = source.BackgroundImageDim;
            Mode = source.Mode;
            Locked = source.Locked;
            DesktopTopMost = source.DesktopTopMost;
            DataDirectory = source.DataDirectory;
        }

        // ---------------------------------------------------------------- helpers

        public string GetMetricName(int metric)
        {
            if (metric < 0 || metric >= MetricNames.Length) return "";
            return NormalizeName(MetricNames[metric], metric);
        }

        public bool IsDefaultMetricName(int metric)
        {
            if (metric < 0 || metric >= MetricNames.Length) return true;
            return string.Equals(GetMetricName(metric), DefaultMetricNames[metric],
                StringComparison.Ordinal);
        }

        public static string NormalizeName(string name, int metric)
        {
            string fallback = (metric >= 0 && metric < DefaultMetricNames.Length)
                ? DefaultMetricNames[metric] : "";
            if (string.IsNullOrEmpty(name)) return fallback;
            string trimmed = name.Trim();
            if (trimmed.Length == 0) return fallback;
            if (trimmed.Length > 10) trimmed = trimmed.Substring(0, 10);
            return trimmed;
        }

        public int[] GetMetricOrder()
        {
            int[] order = MetricOrder;
            if (order == null || order.Length != DefaultMetricOrder.Length) return Defaults();
            bool[] seen = new bool[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                int value = order[i];
                if (value < 0 || value >= order.Length || seen[value]) return Defaults();
                seen[value] = true;
            }
            return order;
        }

        static int[] Defaults()
        {
            return (int[])DefaultMetricOrder.Clone();
        }

        public void MoveMetric(int metric, int delta)
        {
            int[] order = (int[])GetMetricOrder().Clone();
            int position = Array.IndexOf(order, metric);
            int target = position + delta;
            if (position < 0 || target < 0 || target >= order.Length) return;
            order[position] = order[target];
            order[target] = metric;
            MetricOrder = order;
        }

        static string JoinOrder(int[] order)
        {
            string[] parts = new string[order.Length];
            for (int i = 0; i < order.Length; i++)
                parts[i] = order[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }

        static int[] ParseOrder(string text)
        {
            if (string.IsNullOrEmpty(text)) return Defaults();
            string[] parts = text.Split(',');
            if (parts.Length != DefaultMetricOrder.Length) return Defaults();
            int[] order = new int[parts.Length];
            bool[] seen = new bool[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int value;
                if (!int.TryParse(parts[i], out value)) return Defaults();
                if (value < 0 || value >= parts.Length || seen[value]) return Defaults();
                seen[value] = true;
                order[i] = value;
            }
            return order;
        }

        // ---------------------------------------------------------------- reset

        public void ResetStyle()
        {
            FollowSystem = true;
            TransparentStyle = false;
            Background = Color.FromArgb(31, 31, 31);
            TextColor = Color.FromArgb(242, 242, 242);
            LabelColor = Color.FromArgb(162, 162, 162);
            BorderColor = Color.FromArgb(62, 62, 62);
            ValueAlign = 0;
            ColumnGap = 0;
            BackgroundImagePath = "";
            BackgroundImageFit = 0;
            BackgroundImageDim = 35;
        }

        public void ResetMetricNames()
        {
            MetricNames = (string[])DefaultMetricNames.Clone();
        }

        // 基础设置里的“恢复默认设置”：样式、名称、显示内容、顺序、配置目录都回到出厂值，位置保持不变。
        public void ResetToDefaults()
        {
            ResetStyle();
            ResetMetricNames();
            VisibleMask = 0x3F;
            MetricOrder = Defaults();
            DataDirectory = DefaultDirectory();
        }

        public void ResetPosition()
        {
            Mode = DisplayMode.Taskbar;
            Locked = true;
            HasTaskbarX = false;
            TaskbarX = 0;
            DesktopX = -1;
            DesktopY = -1;
        }

        // 旧版本用 bit4 表示“网络”，再用 NetworkDisplayMode 决定一个格子里的内容。
        // 升级后网络拆成三个独立指标：0x10 上传、0x20 下载、0x40 上下行合并。
        static int MigrateVisibleMask(int mask, int version, int legacyNetworkMode)
        {
            if (version >= 2)
                return mask == 0 ? 0x3F : mask;

            int result = mask & 0x0F;
            if ((mask & 0x10) != 0)
            {
                if (legacyNetworkMode == 0) result |= 0x10;
                else if (legacyNetworkMode == 1) result |= 0x20;
                else result |= 0x30;
            }
            return result == 0 ? 0x3F : result;
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "TaskbarMetrics";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(value)) return false;
                    return string.Equals(value.Trim().Trim('"'), Application.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null) throw new InvalidOperationException("无法打开当前用户的启动项注册表。");
                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                }
                else
                {
                    if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    static class AppLog
    {
        static readonly object Gate = new object();
        static string _directory;
        static string _logFile;

        static AppLog()
        {
            SetDirectory(AppSettings.DefaultDirectory());
        }

        public static string DataDirectory
        {
            get { return _directory; }
        }

        // 配置文件目录可能是用户自定义的，日志跟着走。
        public static void SetDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) directory = AppSettings.DefaultDirectory();
            _directory = directory;
            _logFile = Path.Combine(directory, "TaskbarMetrics.log");
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        // 日志（包括 ERROR）写在配置目录；那个目录写不进去时退回默认目录，尽量别把错误丢掉。
        static void Write(string level, string message, Exception ex)
        {
            if (TryWrite(level, message, ex)) return;

            string fallback = AppSettings.DefaultDirectory();
            if (string.Equals(_directory, fallback, StringComparison.OrdinalIgnoreCase)) return;
            SetDirectory(fallback);
            TryWrite(level, message, ex);
        }

        static bool TryWrite(string level, string message, Exception ex)
        {
            try
            {
                lock (Gate)
                {
                    string dir = Path.GetDirectoryName(_logFile);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    FileInfo fi = new FileInfo(_logFile);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = _logFile + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_logFile, old);
                    }
                    using (StreamWriter sw = new StreamWriter(_logFile, true, Encoding.UTF8))
                    {
                        sw.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                        sw.Write(" [");
                        sw.Write(level);
                        sw.Write("] ");
                        sw.WriteLine(message);
                        if (ex != null) sw.WriteLine(ex);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
