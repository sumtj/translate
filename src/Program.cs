using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipTranslate
{
    internal static class Program
    {
        private const int HK_GRAB = 1;   // 划词翻译
        private const int HK_SNIP = 2;   // 截图翻译
        private const int HK_OCR = 3;    // 截图 OCR
        private const int HK_SPEAK = 4;  // 朗读上次
        private const int HK_INPUT = 5;  // 输入翻译

        private static Mutex _mutex;
        private static AppConfig _cfg;
        private static NotifyIcon _tray;
        private static HotKeyWindow _hkWindow;
        private static Icon _icon;
        private static ResultForm _resultForm;
        private static string _lastSource = "";

        [STAThread]
        private static void Main(string[] args)
        {
            // ---- 命令行模式 ----
            if (args != null && args.Length > 0)
            {
                if (args[0] == "--selftest" || args[0] == "/selftest") { SelfTest.Run(args); return; }
                if (args[0] == "--say" && args.Length > 1)
                {
                    // 读配置，这样 ini 里改的 TtsVoice 能直接试出来
                    var c = AppConfig.Load();
                    Speech.Init(c.SpeechRate, c.TtsEngine, c.TtsVoice);
                    Speech.SpeakAndWait(string.Join(" ", args, 1, args.Length - 1));
                    Speech.Shutdown();
                    return;
                }
                if (args[0] == "--translate" && args.Length > 1)
                {
                    SelfTest.TranslateOnce(string.Join(" ", args, 1, args.Length - 1));
                    return;
                }
                if (args[0] == "--autostart")
                {
                    // 命令行控制开机自启，方便脚本化
                    string what = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                    if (what == "on" || what == "enable")
                    {
                        string err = AutoStart.Enable();
                        Console.WriteLine(err == null ? "已开启开机自启" : "失败: " + err);
                    }
                    else if (what == "off" || what == "disable")
                    {
                        string err = AutoStart.Disable();
                        Console.WriteLine(err == null ? "已关闭开机自启" : "失败: " + err);
                    }
                    Console.WriteLine("当前状态: " + (AutoStart.IsEnabled ? "已开启" : "未开启"));
                    return;
                }
            }

            try { Native.SetProcessDPIAware(); }
            catch (Exception ex) { Log.Warn("SetProcessDPIAware 失败: " + ex.Message); }

            bool created;
            _mutex = new Mutex(true, "SnipTranslate_SingleInstance_v2", out created);
            if (!created)
            {
                // 已在运行。不弹窗打扰，直接退出（按需求：不要有窗口弹出来）
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log.Error("UI 线程异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Error("未处理异常: " + e.ExceptionObject);
            };

            _cfg = AppConfig.Load();
            Ocr.Configure(_cfg.OcrLanguage);
            Speech.Init(_cfg.SpeechRate, _cfg.TtsEngine, _cfg.TtsVoice);
            AppPaths.CleanTempDir();   // 清掉上次遗留的中间文件（日志保留）

            _icon = CreateTrayIcon();
            _hkWindow = new HotKeyWindow();
            _hkWindow.HotKeyPressed += OnHotKey;

            BuildTray();
            RegisterHotKeys();

            Log.Info("===== SnipTranslate 启动 | OCR=" + Ocr.EngineLanguage
                     + " | 发音=" + Speech.VoiceName + " =====");
            Log.Info("热键：划词 " + Show(_cfg.HotkeyGrab) + " / 截图 " + Show(_cfg.HotkeySnip)
                     + " / OCR " + Show(_cfg.HotkeyOcr) + " / 朗读 " + Show(_cfg.HotkeySpeak)
                     + " / 输入 " + Show(_cfg.HotkeyInput));

            if (_cfg.StartupTip)
            {
                try
                {
                    _tray.BalloonTipTitle = "SnipTranslate 已启动";
                    _tray.BalloonTipText = "划词 " + _cfg.HotkeyGrab + "　截图 " + _cfg.HotkeySnip
                                           + "　OCR " + _cfg.HotkeyOcr;
                    _tray.ShowBalloonTip(2500);
                }
                catch { }
            }

            ScheduleUiWarmUp();

            Application.Run(_hkWindow);
            Cleanup();
        }

        /// <summary>
        /// 界面预热。第一次弹结果窗时，WinForms 要现场 JIT 一大堆布局/绘制/字体代码，
        /// 会明显卡一下。启动后先在屏幕外造一个窗口把该走的路径都走一遍
        /// （用 DrawToBitmap 强制绘制，不 Show，所以不会闪窗）。
        /// </summary>
        private static void ScheduleUiWarmUp()
        {
            var t = new System.Windows.Forms.Timer();
            t.Interval = 800;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using (var f = new ResultForm("warm up the render path", _cfg, true))
                    {
                        f.CreateControl();
                        using (var bmp = new Bitmap(16, 16))
                            f.DrawToBitmap(bmp, new Rectangle(0, 0, 16, 16));
                    }
                    Log.Info("界面预热完成（" + sw.ElapsedMilliseconds + "ms）");
                }
                catch (Exception ex) { Log.Warn("界面预热失败: " + ex.Message); }
            };
            t.Start();
        }

        // ==================== 托盘 ====================

        private static void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = _icon;
            _tray.Visible = true;
            _tray.Text = TrayTooltip();

            // 注意：左键点击不挂任何事件 —— 点了不弹窗，只右键出菜单（跟 Snipaste 一样）
            RebuildMenu();
        }

        private static void RebuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9f);

            menu.Items.Add(MenuLabel("划词翻译", _cfg.HotkeyGrab), null, delegate { DoGrab(); });
            menu.Items.Add(MenuLabel("截图翻译", _cfg.HotkeySnip), null, delegate { DoSnip(true); });
            menu.Items.Add(MenuLabel("截图 OCR", _cfg.HotkeyOcr), null, delegate { DoSnip(false); });
            menu.Items.Add(MenuLabel("输入翻译", _cfg.HotkeyInput), null, delegate { DoInput(); });
            menu.Items.Add(MenuLabel("朗读上次", _cfg.HotkeySpeak), null, delegate { SpeakLast(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开配置文件", null, delegate { Native.OpenExternal(_cfg.ConfigPath); });
            menu.Items.Add("重新加载配置", null, delegate { ReloadConfig(); });
            menu.Items.Add("查看日志", null, delegate { Native.OpenExternal(Log.LogPath); });

            // 开机自启：勾选状态直接读注册表，所以你在「任务管理器 → 启动」里
            // 手动关掉之后，这里的勾也会跟着消失
            var autoItem = new ToolStripMenuItem("开机自启");
            autoItem.Checked = AutoStart.IsEnabled;
            autoItem.Click += delegate
            {
                string err = AutoStart.IsEnabled ? AutoStart.Disable() : AutoStart.Enable();
                if (err != null)
                    MessageBox.Show("设置失败：" + err, "SnipTranslate",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                autoItem.Checked = AutoStart.IsEnabled;   // 以注册表实际状态为准
            };
            menu.Items.Add(autoItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });

            if (_tray.ContextMenuStrip != null) _tray.ContextMenuStrip.Dispose();
            _tray.ContextMenuStrip = menu;
        }

        // ==================== 热键 ====================

        private static void RegisterHotKeys()
        {
            var specs = new List<HotKeySpec>();
            AddSpec(specs, HK_GRAB, _cfg.HotkeyGrab, "划词翻译");
            AddSpec(specs, HK_SNIP, _cfg.HotkeySnip, "截图翻译");
            AddSpec(specs, HK_OCR, _cfg.HotkeyOcr, "截图OCR");
            AddSpec(specs, HK_SPEAK, _cfg.HotkeySpeak, "朗读上次");
            AddSpec(specs, HK_INPUT, _cfg.HotkeyInput, "输入翻译");

            _hkWindow.Apply(specs, _cfg.UseKeyboardHook);
            Log.Info("热键注册结果：RegisterHotKey 成功 " + _hkWindow.RegisteredCount
                     + " / " + specs.Count + "，键盘钩子 " + (_hkWindow.HookInstalled ? "已启用" : "未启用"));
        }

        private static void AddSpec(List<HotKeySpec> specs, int id, string text, string what)
        {
            // 留空 = 这条不占用快捷键，功能仍可从托盘菜单调用
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                Log.Info(what + "：未设置快捷键，跳过注册（可从托盘菜单调用）");
                return;
            }

            uint mods, vk;
            string err;
            if (!HotKeyParser.TryParse(text, out mods, out vk, out err))
            {
                Log.Warn("热键格式无效: " + what + " = " + text + " (" + err + ")");
                return;
            }
            var spec = new HotKeySpec();
            spec.Id = id;
            spec.Text = text;
            spec.What = what;
            spec.Modifiers = mods;
            spec.Vk = vk;
            specs.Add(spec);
        }

        /// <summary>菜单项文字：有快捷键就把快捷键缀在后面。</summary>
        private static string MenuLabel(string label, string hotkey)
        {
            return string.IsNullOrEmpty(hotkey) ? label : label + "　" + hotkey;
        }

        private static string Show(string hotkey)
        {
            return string.IsNullOrEmpty(hotkey) ? "(未设置)" : hotkey;
        }

        /// <summary>托盘悬停提示。NotifyIcon.Text 最长 63 字符，超了会抛异常。</summary>
        private static string TrayTooltip()
        {
            var sb = new System.Text.StringBuilder("SnipTranslate");
            if (!string.IsNullOrEmpty(_cfg.HotkeyGrab)) sb.Append("　划词 ").Append(_cfg.HotkeyGrab);
            if (!string.IsNullOrEmpty(_cfg.HotkeySnip)) sb.Append("　截图 ").Append(_cfg.HotkeySnip);
            if (!string.IsNullOrEmpty(_cfg.HotkeyOcr)) sb.Append("　OCR ").Append(_cfg.HotkeyOcr);
            if (!string.IsNullOrEmpty(_cfg.HotkeySpeak)) sb.Append("　朗读 ").Append(_cfg.HotkeySpeak);
            if (!string.IsNullOrEmpty(_cfg.HotkeyInput)) sb.Append("　输入 ").Append(_cfg.HotkeyInput);
            string s = sb.ToString();
            return s.Length > 63 ? s.Substring(0, 63) : s;
        }

        /// <summary>
        /// 同一时刻只允许一个热键动作在跑。截图框选是模态窗口，
        /// 期间如果又来一次热键，会再叠一层全屏遮罩（踩过这个坑）。
        /// </summary>
        private static bool _busy;

        private static void OnHotKey(int id)
        {
            if (_busy)
            {
                Log.Info("上一个操作还没结束，忽略这次热键 id=" + id);
                return;
            }

            _busy = true;
            try
            {
                switch (id)
                {
                    case HK_GRAB: DoGrab(); break;
                    case HK_SNIP: DoSnip(true); break;
                    case HK_OCR: DoSnip(false); break;
                    case HK_SPEAK: SpeakLast(); break;
                    case HK_INPUT: DoInput(); break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("热键动作异常", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        // ==================== 三个入口动作 ====================

        private static void DoGrab()
        {
            Log.Info("取词前的前台窗口: " + Native.ForegroundWindowInfo());
            string text = ClipboardHelper.GetSelectedText(_cfg.KeepClipboard, _cfg.ClipboardWaitMs);
            Log.Info("取词结果: " + (text == null ? "(没取到)" : text.Length + " 字"));

            if (string.IsNullOrEmpty(text))
            {
                // 没选中文字就把同一个结果窗以「输入模式」弹出来，自己打字翻译
                Log.Info("没有取到选中的文字 -> 弹出输入窗");
                DoInput();
                return;
            }
            DoTranslate(text);
        }

        /// <summary>translate=true 截图翻译；false 只做 OCR。</summary>
        private static void DoSnip(bool translate)
        {
            if (!Ocr.IsAvailable) { Log.Warn("OCR 引擎不可用，截图功能跳过"); return; }

            Rectangle bounds;
            Bitmap full = null;
            try { full = ScreenCapture.CaptureVirtualScreen(out bounds); }
            catch (Exception ex) { Log.Error("截屏失败", ex); return; }

            Bitmap crop = null;
            try { crop = ScreenCapture.SelectRegion(full, bounds); }
            catch (Exception ex) { Log.Error("框选失败", ex); }
            finally { if (full != null) full.Dispose(); }

            if (crop == null) return;   // 用户取消

            string text = null;
            try
            {
                using (Bitmap scaled = ScreenCapture.Scale(crop, _cfg.OcrScale))
                {
                    text = Ocr.Recognize(scaled);
                }
            }
            catch (Exception ex) { Log.Error("识别失败", ex); }
            finally { crop.Dispose(); }

            if (string.IsNullOrEmpty(text))
            {
                Log.Warn("没识别到文字");
                return;
            }

            string preview = text.Length > 200 ? text.Substring(0, 200) + "…" : text;
            Log.Info("截图识别 " + text.Length + " 字: " + preview);

            _lastSource = text;
            if (translate) DoTranslate(text);
            else ShowOnly(text);
        }

        /// <summary>
        /// 输入模式：弹出来的就是划词翻译那个结果窗，外观/图钉/朗读按钮/定位/关闭方式全都一样，
        /// 唯一区别是原文区可以打字，回车翻译，译文出在同一个窗口里。
        /// </summary>
        private static void DoInput()
        {
            if (_resultForm != null && !_resultForm.IsDisposed)
            {
                try { _resultForm.Close(); } catch { }
            }

            var f = new ResultForm("", _cfg, true, true);
            _resultForm = f;
            f.InputSubmitted += delegate(string text) { DoTranslate(text, f); };
            f.Show();
            f.Activate();
            f.FocusSource();
        }

        private static void SpeakLast()
        {
            if (_lastSource.Length == 0) { Log.Warn("还没有可朗读的内容"); return; }
            Log.Info("朗读: " + _lastSource.Substring(0, Math.Min(60, _lastSource.Length)));
            Speech.Speak(_lastSource);
        }

        // ==================== 结果窗口 ====================

        private static ResultForm NewResultForm(string source, bool translateMode)
        {
            if (_resultForm != null && !_resultForm.IsDisposed)
            {
                try { _resultForm.Close(); } catch { }
            }
            var f = new ResultForm(source, _cfg, translateMode);
            _resultForm = f;
            f.Show();
            f.Activate();
            return f;
        }

        /// <summary>只显示文字，不翻译（截图 OCR 用）。</summary>
        private static void ShowOnly(string text)
        {
            _lastSource = text;
            ResultForm f = NewResultForm(text, false);
            f.FitToContent();
        }

        private static void DoTranslate(string text) { DoTranslate(text, null); }

        /// <param name="reuse">不为 null 就在这个窗口里出结果（输入模式改完再回车时用）。</param>
        private static void DoTranslate(string text, ResultForm reuse)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;

            _lastSource = text;

            ResultForm form;
            if (reuse != null && !reuse.IsDisposed)
            {
                form = reuse;
                form.BeginTranslate(text);   // 同一个窗口，重新显示「翻译中」
            }
            else
            {
                form = NewResultForm(text, true);
            }

            AppConfig cfg = _cfg;
            Task.Factory.StartNew(delegate { return Translators.Translate(text, cfg); })
                .ContinueWith(delegate(Task<TranslateResult> t)
                {
                    try
                    {
                        if (form.IsDisposed) return;
                        form.BeginInvoke(new Action(delegate
                        {
                            if (form.IsDisposed) return;
                            if (t.IsFaulted)
                            {
                                Exception ex = t.Exception == null ? null : t.Exception.GetBaseException();
                                form.ApplyError("翻译出错：" + (ex == null ? "未知错误" : ex.Message));
                            }
                            else
                            {
                                form.ApplyResult(t.Result);
                                if (cfg.AutoSpeak) Speech.Speak(text);
                            }
                        }));
                    }
                    catch (Exception ex) { Log.Error("回填翻译结果失败", ex); }
                });
        }

        // ==================== 配置 / 退出 ====================

        private static void ReloadConfig()
        {
            try
            {
                _cfg = AppConfig.Load();
                Ocr.Configure(_cfg.OcrLanguage);
                Speech.Init(_cfg.SpeechRate, _cfg.TtsEngine, _cfg.TtsVoice);
                RegisterHotKeys();
                RebuildMenu();
                _tray.Text = TrayTooltip();
                Log.Info("配置已重新加载");
            }
            catch (Exception ex) { Log.Error("重新加载配置失败", ex); }
        }

        private static void ExitApp()
        {
            try { if (_hkWindow != null) _hkWindow.Shutdown(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { Speech.Shutdown(); } catch { }
            try { if (_resultForm != null && !_resultForm.IsDisposed) _resultForm.Close(); } catch { }
            Log.Info("===== SnipTranslate 退出 =====");
            Application.Exit();
        }

        private static void Cleanup()
        {
            try { if (_hkWindow != null) _hkWindow.Shutdown(); } catch { }
            try { if (_icon != null) _icon.Dispose(); } catch { }
            try { Speech.Shutdown(); } catch { }
            try { if (_mutex != null) _mutex.ReleaseMutex(); } catch { }
        }

        // ==================== 托盘图标 ====================

        // 编译时嵌进 exe 的多尺寸图标（见 build.ps1），资源名就是这个
        private const string IconResource = "SnipTranslate.app.ico";

        /// <summary>
        /// 优先用编译进 exe 的多尺寸图标：里面 16/20/24/32/.../256 各一份，
        /// Windows 会按当前 DPI 取原生尺寸，不会把 32×32 缩成 16×16 那样糊掉。
        /// 取不到才退回运行时绘制。
        /// </summary>
        private static Icon CreateTrayIcon()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                System.IO.Stream s = asm.GetManifestResourceStream(IconResource);
                if (s != null)
                {
                    using (s)
                    using (var multi = new Icon(s))
                    {
                        Size want = SystemInformation.SmallIconSize;   // 96 DPI 下是 16×16
                        return new Icon(multi, want);
                    }
                }
                Log.Warn("没找到内置图标资源 " + IconResource + "，改用运行时绘制");
            }
            catch (Exception ex)
            {
                Log.Warn("加载内置图标失败，改用运行时绘制: " + ex.Message);
            }
            return DrawFallbackIcon();
        }

        /// <summary>兜底：运行时画一个同款「圆角蓝底 + 白色 A」，不含任何中文。</summary>
        private static Icon DrawFallbackIcon()
        {
            const int S = 32;
            using (var bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, S, S), 8))
                    using (var brush = new LinearGradientBrush(
                               new Rectangle(0, 0, S, S),
                               Color.FromArgb(90, 168, 255),
                               Color.FromArgb(6, 84, 190), 55f))
                    {
                        g.FillPath(brush, path);
                    }

                    using (var font = new Font("Segoe UI", 23f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    })
                    using (var fg = new SolidBrush(Color.White))
                    {
                        g.DrawString("A", font, fg, new RectangleF(0f, 0f, S, S), sf);
                    }
                }

                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
