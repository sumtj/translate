using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SnipTranslate
{
    /// <summary>一条热键绑定。</summary>
    internal class HotKeySpec
    {
        public int Id;
        public string Text;
        public string What;
        public uint Modifiers;
        public uint Vk;

        public bool Ctrl { get { return (Modifiers & Native.MOD_CONTROL) != 0; } }
        public bool Alt { get { return (Modifiers & Native.MOD_ALT) != 0; } }
        public bool Shift { get { return (Modifiers & Native.MOD_SHIFT) != 0; } }
        public bool Win { get { return (Modifiers & Native.MOD_WIN) != 0; } }
    }

    /// <summary>
    /// 热键接收器。两套通道互为备份：
    ///   1. RegisterHotKey —— 标准做法；
    ///   2. WH_KEYBOARD_LL 底层键盘钩子 —— 组合键被输入法或某些常驻软件先吞掉时，
    ///      RegisterHotKey 永远收不到，靠钩子在更早的位置截下来。
    ///      底层钩子是「后装的先被调用」，所以本程序的钩子优先于那些早就在跑的软件。
    ///
    /// 两个必须遵守的约束（都是踩坑换来的）：
    ///   A. **钩子回调必须立刻返回**。回调里同步跑取词（最长 1 秒）会超过 Windows 的
    ///      LowLevelHooksTimeout（默认 300ms），系统会判定钩子超时：不再采纳它的返回值
    ///      —— 于是按键继续传到 RegisterHotKey，一次按键触发两次；严重时直接卸载钩子。
    ///      所以回调只做匹配，动作通过 BeginInvoke 丢回 UI 线程执行。
    ///   B. **钩子装在自己的线程上**。装在 UI 线程上的话，UI 一忙（比如截整屏要 200ms）
    ///      钩子就没人处理，同样会超时。这里单独开一个线程跑消息循环。
    /// </summary>
    internal class HotKeyWindow : Form
    {
        public event Action<int> HotKeyPressed;

        private readonly List<int> _registered = new List<int>();
        private volatile HotKeySpec[] _matchList = new HotKeySpec[0];

        // 这两个集合只在钩子线程里访问，不需要加锁
        private readonly HashSet<int> _downKeys = new HashSet<int>();
        private readonly HashSet<int> _swallowed = new HashSet<int>();

        private Thread _hookThread;
        private uint _hookThreadId;
        private IntPtr _hook = IntPtr.Zero;
        private Native.HookProc _hookProc;          // 必须保留引用，否则被 GC 回收会崩
        private readonly ManualResetEventSlim _hookReady = new ManualResetEventSlim(false);
        private bool _hookOk;

        private int _lastFireTick;
        private const int DedupeMs = 400;

        public bool HookInstalled { get { return _hookOk; } }
        public int RegisteredCount { get { return _registered.Count; } }

        public HotKeyWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            Size = new Size(1, 1);
            Text = "SnipTranslate.HotKeyWindow";
            IntPtr unused = Handle;
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        // ==================== 注册 ====================

        public void Apply(List<HotKeySpec> specs, bool useKeyboardHook)
        {
            UnregisterAll();

            _matchList = specs.ToArray();   // 原子替换，钩子线程立即可见

            bool hookOk = useKeyboardHook && InstallHook();

            // 钩子装上了也照样注册系统热键：正常情况下钩子会把按键吃掉，RegisterHotKey 不会触发；
            // 万一钩子被系统卸载，RegisterHotKey 还能兜住。
            foreach (HotKeySpec s in specs)
            {
                if (Native.RegisterHotKey(Handle, s.Id, s.Modifiers | Native.MOD_NOREPEAT, s.Vk))
                {
                    _registered.Add(s.Id);
                    Log.Info("RegisterHotKey 成功: " + s.What + " = " + s.Text);
                }
                else
                {
                    int code = Marshal.GetLastWin32Error();
                    Log.Warn("RegisterHotKey 失败: " + s.What + " = " + s.Text + "（Win32 " + code + "），靠钩子兜底");
                }
            }

            if (hookOk) Log.Info("键盘钩子已就绪（独立线程 + 异步派发），优先由它截获按键");
        }

        private void UnregisterAll()
        {
            foreach (int id in _registered)
            {
                try { Native.UnregisterHotKey(Handle, id); } catch { }
            }
            _registered.Clear();
        }

        // ==================== 钩子（独立线程）====================

        private bool InstallHook()
        {
            if (_hookOk) return true;
            try
            {
                _hookReady.Reset();
                _hookThread = new Thread(HookThreadProc);
                _hookThread.IsBackground = true;
                _hookThread.Name = "SnipTranslate.KeyboardHook";
                _hookThread.Start();

                // 等线程把钩子装好（最多 2 秒）
                if (!_hookReady.Wait(2000))
                {
                    Log.Warn("键盘钩子线程启动超时");
                    return false;
                }
                return _hookOk;
            }
            catch (Exception ex)
            {
                Log.Error("安装键盘钩子异常", ex);
                return false;
            }
        }

        private void HookThreadProc()
        {
            try
            {
                _hookThreadId = Native.GetCurrentThreadId();
                _hookProc = HookCallback;   // 在本线程创建委托，保证回调也跑在本线程
                _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero)
                {
                    Log.Warn("SetWindowsHookEx 失败（Win32 " + Marshal.GetLastWin32Error() + "），只能靠 RegisterHotKey");
                    _hookOk = false;
                    _hookReady.Set();
                    return;
                }

                _hookOk = true;
                _hookReady.Set();

                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }

                Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                Log.Info("键盘钩子线程退出");
            }
            catch (Exception ex)
            {
                Log.Error("键盘钩子线程异常", ex);
                _hookOk = false;
                try { _hookReady.Set(); } catch { }
            }
        }

        private void UninstallHook()
        {
            if (_hookThread == null) return;
            try
            {
                if (_hookThreadId != 0) Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _hookThread.Join(1000);
            }
            catch { }
            _hookThread = null;
            _hookThreadId = 0;
            _hookOk = false;
            _hookProc = null;
        }

        public void Shutdown()
        {
            UnregisterAll();
            UninstallHook();
        }

        // ==================== 触发 ====================

        /// <summary>本方法可能从钩子线程或 UI 线程调用，内部用时间戳去重。</summary>
        private void Fire(int id, string source)
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastFireTick) < DedupeMs)
            {
                Log.Info("热键 " + id + " 来自 " + source + "，刚触发过，忽略（去重）");
                return;
            }
            _lastFireTick = now;

            Log.Info("热键触发 id=" + id + "（来源：" + source + "）");
            Action<int> handler = HotKeyPressed;
            if (handler == null) return;
            try { handler(id); }
            catch (Exception ex) { Log.Error("热键处理异常", ex); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) { Fire(m.WParam.ToInt32(), "RegisterHotKey"); return; }
            base.WndProc(ref m);
        }

        // ==================== 钩子回调（必须极快返回）====================

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    int vk = (int)data.vkCode;

                    if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                    {
                        if (_downKeys.Add(vk))   // 按住不放会连着来 keydown，只认第一次
                        {
                            int id = Match(vk);
                            if (id != 0)
                            {
                                _swallowed.Add(vk);
                                Dispatch(id);            // 只投递，不在这里干活
                                return (IntPtr)1;        // 吃掉，别让前台程序或别的热键也响应
                            }
                        }
                    }
                    else if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
                    {
                        _downKeys.Remove(vk);
                        if (_swallowed.Remove(vk)) return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex) { Log.Error("键盘钩子回调异常", ex); }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>把动作丢回 UI 线程执行，钩子回调立刻返回。</summary>
        private void Dispatch(int id)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(delegate { Fire(id, "键盘钩子"); }));
            }
            catch (Exception ex) { Log.Error("派发热键失败", ex); }
        }

        private int Match(int vk)
        {
            bool ctrl = Native.IsKeyDown(0x11) || Native.IsKeyDown(0xA2) || Native.IsKeyDown(0xA3);
            bool alt = Native.IsKeyDown(0x12) || Native.IsKeyDown(0xA4) || Native.IsKeyDown(0xA5);
            bool shift = Native.IsKeyDown(0x10) || Native.IsKeyDown(0xA0) || Native.IsKeyDown(0xA1);
            bool win = Native.IsKeyDown(0x5B) || Native.IsKeyDown(0x5C);

            HotKeySpec[] list = _matchList;
            for (int i = 0; i < list.Length; i++)
            {
                HotKeySpec s = list[i];
                if ((int)s.Vk != vk) continue;
                if (s.Ctrl != ctrl || s.Alt != alt || s.Shift != shift || s.Win != win) continue;
                return s.Id;
            }
            return 0;
        }
    }

    /// <summary>把 "Ctrl+D" 这类字符串解析成修饰键与虚拟键码。</summary>
    internal static class HotKeyParser
    {
        public static bool TryParse(string text, out uint modifiers, out uint vk, out string error)
        {
            modifiers = 0;
            vk = 0;
            error = null;

            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                error = "热键为空";
                return false;
            }

            string[] parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;

                switch (p.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": modifiers |= Native.MOD_CONTROL; break;
                    case "alt": modifiers |= Native.MOD_ALT; break;
                    case "shift": modifiers |= Native.MOD_SHIFT; break;
                    case "win":
                    case "windows": modifiers |= Native.MOD_WIN; break;
                    default:
                        uint key;
                        if (!TryParseKey(p, out key))
                        {
                            error = "无法识别的按键：" + p;
                            return false;
                        }
                        vk = key;
                        break;
                }
            }

            if (vk == 0)
            {
                error = "缺少主键（例如 D、O、F1）";
                return false;
            }
            return true;
        }

        private static bool TryParseKey(string p, out uint vk)
        {
            vk = 0;

            if (p.Length == 1)
            {
                char c = char.ToUpperInvariant(p[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
            }

            if (p.Length >= 2 && (p[0] == 'F' || p[0] == 'f'))
            {
                int n;
                if (int.TryParse(p.Substring(1), out n) && n >= 1 && n <= 24)
                {
                    vk = (uint)(0x70 + n - 1);
                    return true;
                }
            }

            try
            {
                object o = Enum.Parse(typeof(Keys), p, true);
                int code = Convert.ToInt32(o);
                if (code != 0) { vk = (uint)code; return true; }
            }
            catch { }

            return false;
        }
    }
}
