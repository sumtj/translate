using System;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SnipTranslate
{
    /// <summary>
    /// 音频播放。两种来源两种解法：
    ///   WAV —— 用 .NET 自带的 SoundPlayer，直接放内存流。
    ///   MP3 —— Edge TTS 现在只吐 MP3，而 SoundPlayer 不认，所以走 winmm 的 MCI 接口。
    ///          MCI 是系统自带能力，不需要任何第三方解码库。
    ///
    /// 踩过的坑：
    ///   1. MCI 必须跑在 STA 线程上（调用方负责），MTA 线程加载驱动会失败。
    ///   2. 不能用 `play ... wait` —— 那样子命令一直阻塞在锁里，停止操作拿不到锁，
    ///      界面会卡死、声音也停不下来。改成不带 wait 的 play + 轮询状态。
    ///   3. **每次播放必须用独立的临时文件和别名**。之前固定用 tts_audio.mp3，
    ///      上一段还在放的时候这个文件被 MCI 占着，下一段写文件直接失败 →
    ///      退回系统语音 → 而且会一直退回（表现就是「声音突然变机械了，还好不了」）。
    /// </summary>
    internal static class AudioPlayer
    {
        private static readonly object Gate = new object();
        private const string AliasPrefix = "sniptts";

        private static int _seq;            // 每次播放一个独立别名
        private static int _gen;            // 播放代数：旧的一次播放不许把新的关掉
        private static string _currentAlias;
        private static string _currentFile;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder ret, int retLen, IntPtr hwnd);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool mciGetErrorString(int errCode, StringBuilder buffer, int bufferLen);

        public static bool IsWav(byte[] data)
        {
            return data != null && data.Length > 12
                   && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
                   && data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E';
        }

        /// <summary>播放并阻塞到结束或被停止（调用方在 STA 线程上跑）。</summary>
        public static void PlayAndWait(byte[] data)
        {
            if (data == null || data.Length == 0) return;

            if (IsWav(data))
            {
                using (var sp = new SoundPlayer(new MemoryStream(data)))
                {
                    sp.PlaySync();
                }
                return;
            }

            PlayMci(data);
        }

        private static void PlayMci(byte[] data)
        {
            // 每次一个独立文件 + 独立别名，上一段没放完也不影响这一段的开始
            string alias = AliasPrefix + (Interlocked.Increment(ref _seq));
            string file = Path.Combine(AppPaths.TempDir,
                "tts_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".mp3");
            File.WriteAllBytes(file, data);

            int myGen;
            lock (Gate)
            {
                _gen++;
                myGen = _gen;
                _currentAlias = alias;
                _currentFile = file;

                string e1 = Mci(string.Format("open \"{0}\" type mpegvideo alias {1}", file, alias));
                if (e1 != null)
                {
                    string e2 = Mci(string.Format("open \"{0}\" alias {1}", file, alias));
                    if (e2 != null) throw new Exception("mpegvideo 方式: " + e1 + " / 自动方式: " + e2);
                }

                Mci("play " + alias);    // 注意：不带 wait，立即返回
            }

            // 轮询播放状态，这样 Stop() 随时能插进来把声音掐掉。
            // 48kbps 单声道 ≈ 每秒 6000 字节，再加 15 秒余量兜底。
            long maxMs = 15000 + (data.Length / 6);
            var sw = Stopwatch.StartNew();
            string why = "轮询超时";

            try
            {
                while (sw.ElapsedMilliseconds < maxMs)
                {
                    lock (Gate)
                    {
                        if (myGen != _gen) { why = "被停止或取代"; return; }
                        string mode = MciQuery("status " + alias + " mode");
                        if (mode == null) { why = "设备查询失败"; return; }
                        if (mode.IndexOf("stopped", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            why = "自然播完";
                            return;
                        }
                    }
                    Thread.Sleep(40);
                }
            }
            finally
            {
                // 收尾必须由「开设备的这个线程」来做。
                // MCI 的别名不能跨线程用：从别的线程发 stop/close 会报
                // MCIERR_INVALID_DEVICE_NAME(263) —— 命令失败，声音就一直放下去，
                // 文件也被一直占着，于是后面的朗读写不进文件、全部退回系统语音。
                lock (Gate)
                {
                    Mci("stop " + alias);
                    Mci("close " + alias);
                    if (_currentAlias == alias) _currentAlias = null;
                }
                TryDelete(file);
                Log.Info("播放结束（" + why + "）");
            }
        }

        /// <summary>
        /// 中断当前播放。会被界面线程调用，所以绝不能阻塞，也不能在这里发 MCI 命令
        /// （跨线程的别名是无效的）。这里只改代数，让播放线程自己发现并收尾。
        /// </summary>
        public static void Stop()
        {
            lock (Gate)
            {
                if (_currentAlias == null) return;
                _gen++;
                Log.Info("已请求停止播放 " + _currentAlias);
            }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* 设备还没完全松手就算了，下次启动会清 */ }
        }

        /// <summary>清掉历史上遗留的临时音频文件。</summary>
        public static void CleanUpTempFiles()
        {
            try
            {
                foreach (string f in Directory.GetFiles(AppPaths.TempDir, "tts_*.mp3"))
                    TryDelete(f);
            }
            catch { }
        }

        /// <summary>发一条 MCI 命令。成功返回 null，失败返回可读的错误描述。</summary>
        private static string Mci(string command)
        {
            var sb = new StringBuilder(512);
            int rc = mciSendString(command, sb, sb.Capacity, IntPtr.Zero);
            if (rc == 0) return null;

            var err = new StringBuilder(512);
            string text = mciGetErrorString(rc, err, err.Capacity) ? err.ToString() : "未知错误";
            if (sb.Length > 0) text += "（" + sb + "）";
            return "MCI " + rc + ": " + text;
        }

        /// <summary>发一条会返回文本的 MCI 查询，失败返回 null。</summary>
        private static string MciQuery(string command)
        {
            var sb = new StringBuilder(256);
            int rc = mciSendString(command, sb, sb.Capacity, IntPtr.Zero);
            return rc == 0 ? sb.ToString() : null;
        }
    }
}
