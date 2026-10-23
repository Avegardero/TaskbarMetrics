using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarMetrics
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length >= 2 &&
                string.Equals(args[0], "--preview", StringComparison.OrdinalIgnoreCase))
            {
                Preview(args[1], args.Length >= 3 ? args[2] : null);
                return;
            }

            // 开发用：把设置窗口渲染成 PNG，便于截图核对排版。
            if (args != null && args.Length >= 2 &&
                string.Equals(args[0], "--shot", StringComparison.OrdinalIgnoreCase))
            {
                Shot(args[1], args.Length >= 3 ? args[2] : "0");
                return;
            }

            bool createdNew = false;
            using (Mutex mutex = new Mutex(true, @"Local\TaskbarMetrics.Widget", out createdNew))
            {
                if (!createdNew) return;

                try
                {
                    Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PerMonitorV2
                }
                catch
                {
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                try
                {
                    Application.Run(new WidgetForm());
                }
                catch (Exception ex)
                {
                    AppLog.Error("Fatal UI error", ex);
                    MessageBox.Show("任务栏性能组件启动失败：\n" + ex.Message, "任务栏性能",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // 把面板渲染成 PNG，方便在没有任务栏的环境下检查排版：TaskbarMetrics.exe --preview out.png
        static void Preview(string path, string heightArg)
        {
            int height = 40;
            if (!string.IsNullOrEmpty(heightArg)) int.TryParse(heightArg, out height);
            if (height < 16) height = 40;
            try
            {
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch
            {
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (WidgetForm form = new WidgetForm())
            {
                Size size = form.MeasurePanelSize(height);
                using (Bitmap bitmap = form.BuildSurface(size.Width, size.Height))
                {
                    string full = Path.GetFullPath(path);
                    string dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    bitmap.Save(full, ImageFormat.Png);
                }
            }
        }

        // 开发用：TaskbarMetrics.exe --shot out.png [页码]，把设置窗口的某一页截图。
        static void Shot(string path, string pageArg)
        {
            int page = 0;
            int.TryParse(pageArg, out page);
            try
            {
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch
            {
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (StyleDialog dialog = new StyleDialog(AppSettings.Load(), null, delegate { }, null))
            {
                dialog.Show();
                dialog.ShowPage(page);
                Application.DoEvents();
                using (Bitmap bitmap = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, dialog.Width, dialog.Height));
                    bitmap.Save(path, ImageFormat.Png);
                }
                dialog.Hide();
            }
        }

        static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            AppLog.Error("UI exception", e.Exception);
            MessageBox.Show("任务栏性能组件遇到错误：\n" + e.Exception.Message,
                "任务栏性能", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            AppLog.Error("Unhandled exception", e.ExceptionObject as Exception);
        }
    }
}
