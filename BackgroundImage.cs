using System;
using System.Drawing;
using System.IO;

namespace TaskbarMetrics
{
    static class BackgroundImageStore
    {
        static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

        public static string Import(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("找不到图片文件。", sourcePath);

            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            bool allowed = false;
            for (int i = 0; i < AllowedExtensions.Length; i++)
            {
                if (AllowedExtensions[i] == extension) { allowed = true; break; }
            }
            if (!allowed) throw new InvalidOperationException("只支持 PNG、JPG、BMP、GIF 图片。");

            Directory.CreateDirectory(AppLog.DataDirectory);
            // 用唯一文件名，取消编辑时不会破坏正在使用的背景图；旧文件在保存时统一清理。
            string target = Path.Combine(AppLog.DataDirectory,
                "background-" + DateTime.Now.Ticks.ToString() + extension);
            try
            {
                File.Copy(sourcePath, target, true);
            }
            catch (UnauthorizedAccessException)
            {
                // 某些安全软件/受限环境会拦截 File.Copy，退回逐字节写入再试一次。
                File.WriteAllBytes(target, File.ReadAllBytes(sourcePath));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法把图片复制到 " + AppLog.DataDirectory + "：" + ex.Message);
            }
            return target;
        }

        public static void Delete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string full = Path.GetFullPath(path);
                string data = Path.GetFullPath(AppLog.DataDirectory);
                if (full.StartsWith(data, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(full).StartsWith("background.", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(full)) File.Delete(full);
                }
            }
            catch
            {
            }
        }

        public static Bitmap Load(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image image = Image.FromStream(stream))
            {
                return new Bitmap(image);
            }
        }

        // 保存设置后清掉不再使用的背景图（包括旧版本留下的 background.png 等）。
        public static void RemoveOrphans(string keepPath)
        {
            try
            {
                string keep = string.IsNullOrEmpty(keepPath)
                    ? ""
                    : Path.GetFullPath(keepPath);
                foreach (string file in Directory.GetFiles(AppLog.DataDirectory, "background*"))
                {
                    if (string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { File.Delete(file); } catch { }
                }
            }
            catch
            {
            }
        }
    }
}
