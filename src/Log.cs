using System;
using System.IO;
using System.Text;

namespace SnipTranslate
{
    /// <summary>轻量日志，写到 %TEMP%\SnipTranslate\app.log。热键排查全靠它。</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string LogPath
        {
            get { return Path.Combine(AppPaths.TempDir, "app.log"); }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        public static void Error(string msg, Exception ex)
        {
            Write("ERROR", msg + " | " + ex.GetType().Name + ": " + ex.Message);
        }

        private static volatile string _lastWarning = "";

        /// <summary>最近一条警告（自检用来判断有没有悄悄退回系统语音）。</summary>
        public static string LastWarning { get { return _lastWarning; } }

        public static void ClearLastWarning() { _lastWarning = ""; }

        private static void Write(string level, string msg)
        {
            try
            {
                if (level == "WARN " || level == "ERROR") _lastWarning = msg;
                lock (Gate)
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        try { File.Delete(LogPath); } catch { }
                    }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                                  + " [" + level + "] " + msg + Environment.NewLine;
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    /// <summary>程序用到的路径。</summary>
    internal static class AppPaths
    {
        public static string ExeDir
        {
            get
            {
                try
                {
                    string p = System.Reflection.Assembly.GetEntryAssembly().Location;
                    if (!string.IsNullOrEmpty(p)) return Path.GetDirectoryName(p);
                }
                catch { }
                return Environment.CurrentDirectory;
            }
        }

        public static string TempDir
        {
            get
            {
                string d = Path.Combine(Path.GetTempPath(), "SnipTranslate");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>
        /// 清掉上次运行遗留的中间文件。日志保留，其余一律删掉。
        ///
        /// 正常情况下每个中间文件都是「用完即删」的：
        ///   ocr_*.png   —— OCR 的 finally 里删
        ///   tts_*.mp3   —— 播放结束后删
        /// 这里只是兜底：万一上次是被强杀的、或者删的时候文件还被占着，启动时收一次尾。
        /// </summary>
        public static void CleanTempDir()
        {
            try
            {
                foreach (string f in Directory.GetFiles(TempDir))
                {
                    string name = Path.GetFileName(f);
                    if (string.Equals(name, "app.log", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }
    }
}
