using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SnipTranslate
{
    /// <summary>
    /// 取词与剪贴板操作 —— **全部走 Win32 原生 API，不碰 WinForms 的 Clipboard**。
    ///
    /// 踩过的坑：System.Windows.Forms.Clipboard 底层是 OLE 剪贴板跨进程调用。两个严重后果：
    ///   1. 轮询 ContainsText() 每次 200ms 以上，取一次词要 5 秒；
    ///   2. 遇到残留/无响应的剪贴板属主时会一直阻塞，界面直接卡死。
    /// 原生 API 是同步且廉价的，配合 GetClipboardSequenceNumber() 判断剪贴板有没有变，
    /// 只在变的那一刻才去读。实测取词从 5127ms 降到 102ms。
    /// </summary>
    internal static class ClipboardHelper
    {
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        /// <summary>原生读取剪贴板 Unicode 文本；没有文本返回 null。</summary>
        public static string ReadClipboardTextRaw()
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
                        IntPtr handle = GetClipboardData(CF_UNICODETEXT);
                        if (handle == IntPtr.Zero) return null;
                        IntPtr ptr = GlobalLock(handle);
                        if (ptr == IntPtr.Zero) return null;
                        try { return Marshal.PtrToStringUni(ptr); }
                        finally { GlobalUnlock(handle); }
                    }
                    finally { CloseClipboard(); }
                }
                Thread.Sleep(10);
            }
            return null;
        }

        /// <summary>原生写入剪贴板文本；空串表示只清空。</summary>
        public static bool SetClipboardTextRaw(string text)
        {
            if (text == null) text = "";

            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(10); continue; }
                try
                {
                    if (!EmptyClipboard()) return false;
                    if (text.Length == 0) return true;

                    int bytes = (text.Length + 1) * 2; // UTF-16 + 结尾 \0
                    IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hMem == IntPtr.Zero) return false;

                    IntPtr dst = GlobalLock(hMem);
                    if (dst == IntPtr.Zero) { GlobalFree(hMem); return false; }
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, dst, text.Length);
                        Marshal.WriteInt16(dst, text.Length * 2, 0);
                    }
                    finally { GlobalUnlock(hMem); }

                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        return false;
                    }
                    return true; // 成功后 hMem 归系统所有，不能再释放
                }
                finally { CloseClipboard(); }
            }
            return false;
        }

        /// <summary>等剪贴板变化的默认预算（毫秒）。见 <see cref="GetSelectedText(bool,int)"/> 的说明。</summary>
        public const int DefaultWaitMs = 100;

        /// <summary>
        /// 取当前选中文字：备份 → 模拟 Ctrl+C → 等序号变化 → 读取 → 还原。
        /// </summary>
        /// <param name="waitMs">
        /// 等剪贴板变化的预算，**按真实时间算**。这个值直接决定「没选中文字」时按快捷键
        /// 要卡多久才弹输入窗，所以调得比较激进：实测只要真有选中内容，所有应用
        /// （浏览器、记事本、Electron）都是 **25ms** 内就复制好了，150ms 已是 6 倍余量。
        /// 以前是 1000ms，没选中时要白等满一秒，感觉就是「卡住了」。
        /// 万一碰到特别慢的程序取不到词，把这个值调大即可。
        /// </param>
        public static string GetSelectedText(bool restoreClipboard, int waitMs)
        {
            var total = Stopwatch.StartNew();
            long tBackup, tRelease, tPoll;

            string backup = ReadClipboardTextRaw();
            bool hadBackup = !string.IsNullOrEmpty(backup);
            tBackup = total.ElapsedMilliseconds;

            // 注意：这里**不清空剪贴板**。
            // 判断「有没有复制成功」靠的是序号有没有变，跟清不清空无关；
            // 清空反而多两次剪贴板操作（还要再还原），既慢又白白动用户的东西。
            uint seqBefore = GetClipboardSequenceNumber();

            Native.ReleaseModifiers();
            tRelease = total.ElapsedMilliseconds;

            Native.SendCopy();

            if (waitMs < 30) waitMs = 30;

            string text = null;
            var clock = Stopwatch.StartNew();
            // 用 Stopwatch 而不是自己累加计数：Windows 默认定时器精度约 15.6ms，
            // Sleep(10) 实际会睡 15.6ms，按计数算预算会比真实时间短一大截。
            while (clock.ElapsedMilliseconds < waitMs)
            {
                // 先查再等：Ctrl+C 一般已经生效了，能省掉一轮
                if (GetClipboardSequenceNumber() != seqBefore)
                {
                    text = ReadClipboardTextRaw();
                    if (!string.IsNullOrEmpty(text)) break;
                }
                Thread.Sleep(2);
            }
            tPoll = total.ElapsedMilliseconds;

            // 只有真的复制到东西、剪贴板确实被改过，才需要还原。
            // 没取到词说明剪贴板没被动过，还原纯属浪费。
            if (restoreClipboard && text != null && !SetClipboardTextRaw(hadBackup ? backup : ""))
                Log.Warn("还原剪贴板失败");

            Log.Info(string.Format(
                "取词耗时 {0}ms（备份 {1} + 释放修饰键 {2} + 等序号 {3}/{4} + 还原 {5}，拿到 {6} 字）",
                total.ElapsedMilliseconds, tBackup, tRelease - tBackup, tPoll - tRelease, waitMs,
                total.ElapsedMilliseconds - tPoll, text == null ? 0 : text.Length));

            return text;
        }

        /// <summary>取当前选中文字，用默认预算。</summary>
        public static string GetSelectedText(bool restoreClipboard)
        {
            return GetSelectedText(restoreClipboard, DefaultWaitMs);
        }

        public static bool SetText(string text)
        {
            bool ok = SetClipboardTextRaw(text);
            if (!ok) Log.Warn("写入剪贴板失败");
            return ok;
        }
    }
}
