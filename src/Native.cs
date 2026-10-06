using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SnipTranslate
{
    /// <summary>Win32 互操作集中地：全局热键、底层键盘钩子、模拟按键、DPI。</summary>
    internal static class Native
    {
        // ---- 全局热键 ----
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ---- 底层键盘钩子 ----
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        // ---- 独立线程上的消息循环（给键盘钩子用）----
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        public const uint WM_QUIT = 0x0012;

        // ---- 键盘 ----
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public static bool IsKeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        public const byte VK_SHIFT = 0x10;
        public const byte VK_CONTROL = 0x11;
        public const byte VK_MENU = 0x12;
        public const byte VK_LWIN = 0x5B;
        public const byte VK_RWIN = 0x5C;
        public const byte VK_C = 0x43;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        private static void KeyUp(byte vk) { keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); }
        private static void KeyDown(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); }

        /// <summary>
        /// 热键触发时用户往往还按着 Ctrl/Alt，直接发 Ctrl+C 会变成 Ctrl+Alt+C，目标程序收不到。
        /// 先把手上的修饰键全部弹起。
        /// </summary>
        public static void ReleaseModifiers()
        {
            ReleaseModifiers(ModifierSettleMs);
        }

        /// <param name="settleMs">
        /// 弹起修饰键后等多久再发 Ctrl+C。key-up 和后面的 Ctrl+C 都走同一条输入队列，
        /// 顺序本来就有保证，所以这个等待不是必须的；实测 0ms 也能正常取词。
        /// 留个可调参数是为了万一某个程序需要间隔。
        /// </param>
        public static void ReleaseModifiers(int settleMs)
        {
            bool released = false;
            if (IsKeyDown(VK_MENU)) { KeyUp(VK_MENU); released = true; }
            if (IsKeyDown(VK_SHIFT)) { KeyUp(VK_SHIFT); released = true; }
            if (IsKeyDown(VK_LWIN)) { KeyUp(VK_LWIN); released = true; }
            if (IsKeyDown(VK_RWIN)) { KeyUp(VK_RWIN); released = true; }
            if (IsKeyDown(VK_CONTROL)) { KeyUp(VK_CONTROL); released = true; }

            // 没按着任何修饰键就完全不用等
            if (released && settleMs > 0) Thread.Sleep(settleMs);
        }

        /// <summary>弹起修饰键后等多久。0 = 不等（默认，实测够用）。</summary>
        public static int ModifierSettleMs = 0;

        public static void SendCopy()
        {
            KeyDown(VK_CONTROL);
            KeyDown(VK_C);
            KeyUp(VK_C);
            KeyUp(VK_CONTROL);
        }

        // ---- 其它 ----
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        /// <summary>当前前台窗口的「标题 (进程名)」，排查取词取不到时用。</summary>
        public static string ForegroundWindowInfo()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return "(没有前台窗口)";
                var sb = new StringBuilder(256);
                GetWindowTextW(h, sb, sb.Capacity);
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                string proc = "?";
                try { proc = Process.GetProcessById((int)pid).ProcessName; }
                catch { }
                return "\"" + sb.ToString() + "\" (" + proc + ")";
            }
            catch (Exception ex) { return "(查询失败: " + ex.Message + ")"; }
        }

        public static void OpenExternal(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("OpenExternal 失败: " + ex.Message); }
        }
    }
}
