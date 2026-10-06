using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SnipTranslate
{
    /// <summary>
    /// 命令行自检：SnipTranslate.exe --selftest [图片路径]
    /// 验证 OCR、抓屏、发音、各翻译源、词典。结果打印并写入 %TEMP%\SnipTranslate\selftest.txt。
    /// </summary>
    internal static class SelfTest
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        private const int ATTACH_PARENT_PROCESS = -1;

        // ==================== 压力测试：看内存会不会一直涨 ====================

        private static void Stress(int n, string only)
        {
            bool doOcr = only == "all" || only == "ocr";
            bool doForm = only == "all" || only == "form";
            bool doNet = only == "all" || only == "net";
            bool doSpeak = only == "all" || only == "speak";
            var sb = new StringBuilder();
            Action<string> w = delegate(string s)
            {
                sb.AppendLine(s);
                try { Console.WriteLine(s); } catch { }
                try
                {
                    File.AppendAllText(Path.Combine(AppPaths.TempDir, "stress.txt"),
                        s + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            };

            try { File.Delete(Path.Combine(AppPaths.TempDir, "stress.txt")); } catch { }

            AppConfig cfg = AppConfig.Load();
            Speech.Init(cfg.SpeechRate, cfg.TtsEngine, cfg.TtsVoice);
            Ocr.Configure(cfg.OcrLanguage);

            var me = Process.GetCurrentProcess();

            w("=== 压力测试：跑 " + n + " 轮 ===");
            w("每轮包含：截图 OCR / 建结果窗+关掉 / 翻译一次 / 每 5 轮合成一次发音");
            w("");
            w(string.Format("{0,-6} {1,8} {2,8} {3,7} {4,7} {5,7} {6,7} {7,6} {8,6}",
                "轮次", "私有MB", "工作集", "总句柄", "GDI", "USER", "线程", "临时", "内存MB"));
            w(new string('-', 60));

            Dump(w, me, 0);

            for (int i = 1; i <= n; i++)
            {
                if (doOcr)
                {
                    try
                    {
                        // 1) OCR（内部会写 ocr_*.png 再删掉）
                        using (Bitmap bmp = MakeSample())
                            Ocr.Recognize(bmp);
                    }
                    catch (Exception ex) { w("  第" + i + "轮 OCR 失败: " + ex.Message); }
                }

                if (doForm)
                {
                    try
                    {
                        // 2) 结果窗的建立与销毁
                        using (var f = new ResultForm("Round " + i + " computing has changed everything.", cfg, true))
                        {
                            var r = new TranslateResult();
                            r.Ok = true; r.Text = "第 " + i + " 轮译文。"; r.Engine = "test";
                            f.ApplyResult(r);
                        }
                    }
                    catch (Exception ex) { w("  第" + i + "轮窗口失败: " + ex.Message); }
                }

                if (doNet)
                {
                    try
                    {
                        // 3) 翻译（走真实网络路径）
                        Translators.Translate("The quick brown fox jumps over the lazy dog.", cfg);
                    }
                    catch (Exception ex) { w("  第" + i + "轮翻译失败: " + ex.Message); }
                }

                // 4) 每 5 轮合成一次发音（验证音频缓存不会堆积）
                if (doSpeak && i % 5 == 0)
                {
                    try { Speech.Prefetch("Round number " + i + " checking for memory growth."); }
                    catch { }
                }

                if (i % 5 == 0 || i == n)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Dump(w, me, i);
                }
            }

            w("");
            w("说明：私有内存/workingset 有波动是正常的（GC 有自己的节奏）。");
            w("只要不随轮次单调上涨，就说明没有泄漏。");
            w("临时文件那一列始终应该是 0（日志除外）。");

            Speech.Shutdown();
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.TempDir, "stress.txt"),
                    sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static void Dump(Action<string> w, Process me, int round)
        {
            me.Refresh();
            int files = 0;
            long bytes = 0;
            try
            {
                foreach (string f in Directory.GetFiles(AppPaths.TempDir))
                {
                    string name = Path.GetFileName(f);
                    if (name.Equals("app.log", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("stress.txt", StringComparison.OrdinalIgnoreCase)) continue;
                    files++;
                    bytes += new FileInfo(f).Length;
                }
            }
            catch { }

            w(string.Format("{0,-6} {1,8:N1} {2,8:N1} {3,7} {4,7} {5,7} {6,7} {7,6} {8,6:N0}",
                round == 0 ? "起始" : round.ToString(),
                me.PrivateMemorySize64 / 1048576.0,
                me.WorkingSet64 / 1048576.0,
                me.HandleCount,
                GetGuiResources(me.Handle, 0),   // GDI 对象
                GetGuiResources(me.Handle, 1),   // USER 对象
                me.Threads.Count,
                files,
                GC.GetTotalMemory(false) / 1048576.0));
        }

        public static void Run(string[] args)
        {
            // --stress N：反复跑各条路径，看内存会不会一直涨
            if (args.Length > 1 && args[1].StartsWith("stress", StringComparison.OrdinalIgnoreCase))
            {
                int n = 30;
                if (args.Length > 2) int.TryParse(args[2], out n);
                if (n < 1) n = 1;
                string only = args.Length > 3 ? args[3].ToLowerInvariant() : "all";
                Stress(n, only);
                return;
            }
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }

            var sb = new StringBuilder();
            Action<string> w = delegate(string s)
            {
                sb.AppendLine(s);
                try { Console.WriteLine(s); } catch { }
            };

            w("=== SnipTranslate 自检 ===");
            w("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            AppConfig cfg;
            try
            {
                cfg = AppConfig.Load();
                w("配置: " + cfg.ConfigPath);
                w("热键: 划词 " + cfg.HotkeyGrab + " / 截图 " + cfg.HotkeySnip
                  + " / OCR " + cfg.HotkeyOcr + " / 朗读 " + cfg.HotkeySpeak
                  + " / 输入 " + cfg.HotkeyInput);
            }
            catch (Exception ex) { w("读取配置失败: " + ex.Message); cfg = new AppConfig(); }

            // 热键能否解析
            w("");
            w("---------- 热键解析 ----------");
            foreach (var pair in new[] {
                new object[]{"划词", cfg.HotkeyGrab}, new object[]{"截图", cfg.HotkeySnip},
                new object[]{"OCR", cfg.HotkeyOcr}, new object[]{"朗读", cfg.HotkeySpeak},
                new object[]{"输入", cfg.HotkeyInput} })
            {
                uint m, k; string err;
                bool ok = HotKeyParser.TryParse((string)pair[1], out m, out k, out err);
                w(string.Format("  {0,-4} {1,-12} -> {2}", pair[0], pair[1],
                    ok ? ("OK  修饰=" + m + " 主键=0x" + k.ToString("X2")) : ("失败 " + err)));
            }

            // OCR
            w("");
            w("---------- OCR ----------");
            Ocr.Configure(cfg.OcrLanguage);
            string samplePath = args.Length > 1 ? args[1] : null;
            string ocrText = "";
            try
            {
                w("引擎语言: " + Ocr.EngineLanguage + "   可用: " + Ocr.IsAvailable);
                w("系统已装识别语言: " + string.Join(", ", Ocr.AvailableLanguages().ToArray()));

                if (samplePath == null || !File.Exists(samplePath))
                {
                    samplePath = Path.Combine(AppPaths.TempDir, "selftest_sample.png");
                    using (var bmp = MakeSample()) bmp.Save(samplePath, System.Drawing.Imaging.ImageFormat.Png);
                    w("已生成测试图: " + samplePath);
                }

                using (var bmp = new Bitmap(samplePath))
                {
                    var sw = Stopwatch.StartNew();
                    ocrText = Ocr.Recognize(bmp);
                    sw.Stop();
                    w("识别耗时: " + sw.ElapsedMilliseconds + " ms");
                    w("识别结果: " + ocrText);

                    w("");
                    w("放大倍数对比（原文: The quick brown fox jumps over the lazy dog. ...）");
                    for (int scale = 1; scale <= 3; scale++)
                    {
                        using (Bitmap up = ScreenCapture.Scale(bmp, scale))
                        {
                            var sw2 = Stopwatch.StartNew();
                            string t = Ocr.Recognize(up);
                            sw2.Stop();
                            int hits = 0;
                            string[] probes = { "fox jumps over", "accurate and fast",
                                                "button below to continue", "404: file not found",
                                                "smaller line to test" };
                            foreach (string p in probes)
                                if (t.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) hits++;
                            w(string.Format("  {0}x  {1,4}ms  正确片段 {2}/5  | {3}", scale, sw2.ElapsedMilliseconds, hits, t));
                        }
                    }
                }
            }
            catch (Exception ex) { w("OCR 失败: " + ex.GetType().Name + ": " + ex.Message); }

            // 屏幕抓取
            w("");
            w("---------- 屏幕抓取 ----------");
            try
            {
                Rectangle vb;
                using (Bitmap screen = ScreenCapture.CaptureVirtualScreen(out vb))
                {
                    w("虚拟桌面: " + vb + "   抓取尺寸: " + screen.Width + " x " + screen.Height);
                    int cw = Math.Min(900, screen.Width), ch = Math.Min(300, screen.Height);
                    using (var crop = new Bitmap(cw, ch, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(crop))
                            g.DrawImage(screen, new Rectangle(0, 0, cw, ch), new Rectangle(0, 0, cw, ch), GraphicsUnit.Pixel);
                        using (Bitmap up = ScreenCapture.Scale(crop, cfg.OcrScale))
                        {
                            var sw = Stopwatch.StartNew();
                            string t = Ocr.Recognize(up);
                            sw.Stop();
                            w(string.Format("左上角 {0}x{1} 放大 {2}x 识别耗时 {3}ms", cw, ch, cfg.OcrScale, sw.ElapsedMilliseconds));
                            w("识别到: " + (t.Length == 0 ? "(这一块没有文字，正常)" : t));
                        }
                    }
                }
            }
            catch (Exception ex) { w("屏幕抓取失败: " + ex.GetType().Name + ": " + ex.Message); }

            // 发音
            w("");
            w("---------- 发音语音 ----------");
            try
            {
                Speech.Init(cfg.SpeechRate, cfg.TtsEngine, cfg.TtsVoice);
                w("发音引擎: " + Speech.EngineName);
                w("当前语音: " + Speech.VoiceName);
                w("系统自带的 SAPI 语音:");
                foreach (string v in Speech.ListSapiVoices()) w("  " + v);
                w("Edge 可用的英文女声（需要联网）:");
                var edge = Speech.ListEdgeVoices(10000);
                if (edge.Count == 0) w("  (没拉到，可能断网)");
                else foreach (string v in edge) w("  " + v);
            }
            catch (Exception ex) { w("语音初始化失败: " + ex.Message); }

            // 翻译源
            w("");
            w("---------- 翻译源 ----------");
            string probe = ocrText.Length > 0 ? ocrText : "Translation should be accurate and fast.";
            if (probe.Length > 300) probe = probe.Substring(0, 300);
            w("测试文本: " + probe);
            w("");
            foreach (string engine in new[] { "youdao", "google", "mymemory" })
            {
                var one = new AppConfig();
                one.EngineOrder = engine;
                one.ShowPhonetic = false;
                one.TimeoutMs = cfg.TimeoutMs;

                var sw = Stopwatch.StartNew();
                TranslateResult r;
                try { r = Translators.Translate(probe, one); }
                catch (Exception ex) { w(string.Format("[{0,-9}] 异常  {1}", engine, ex.Message)); continue; }
                sw.Stop();

                if (r.Ok) w(string.Format("[{0,-9}] OK   {1,5}ms  -> {2}", engine, sw.ElapsedMilliseconds, r.Text));
                else w(string.Format("[{0,-9}] FAIL {1,5}ms  {2}", engine, sw.ElapsedMilliseconds, r.Error.Replace("\n", " ; ")));
            }

            // 词典
            w("");
            w("---------- 词典（单词）----------");
            var wordCfg = new AppConfig();
            wordCfg.EngineOrder = "youdao";
            wordCfg.ShowPhonetic = true;
            try
            {
                TranslateResult r = Translators.Translate("translation", wordCfg);
                w("  美式音标: " + (r.PhoneticUS.Length > 0 ? "/" + r.PhoneticUS + "/" : "(无)"));
                w("  译文: " + r.Text);
                foreach (string d in r.Definitions) w("  释义: " + d);
            }
            catch (Exception ex) { w("词典查询失败: " + ex.Message); }

            // 窗口自适应高度 + 朗读
            w("");
            w("---------- 结果窗口 ----------");
            try
            {
                using (var f = new ResultForm("Computing has fundamentally changed the way we learn languages.", cfg, true))
                {
                    var r = new TranslateResult();
                    r.Ok = true; r.Text = "计算从根本上改变了我们学习语言的方式。"; r.Engine = "Google";
                    r.PhoneticUS = "";
                    f.ApplyResult(r);
                    w(string.Format("  短内容 -> 窗口 {0} x {1}", f.Width, f.Height));

                    // 把窗口渲染成图片存下来，方便核对图钉 / 朗读按钮 / 配色。
                    // 必须先在屏幕外 Show 一次，否则 DrawToBitmap 画不出子控件（只会得到一张空背景）。
                    try
                    {
                        f.SuppressAutoPlacement = true;
                        f.StartPosition = FormStartPosition.Manual;
                        f.Location = new Point(-4000, -4000);
                        f.Show();
                        Application.DoEvents();
                        using (var bmp = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                            string prev = Path.Combine(AppPaths.TempDir, "preview.png");
                            bmp.Save(prev, System.Drawing.Imaging.ImageFormat.Png);
                            w("  预览图已保存: " + prev);
                        }
                        f.Hide();
                    }
                    catch (Exception ex) { w("  预览图生成失败: " + ex.Message); }

                    var r2 = new TranslateResult();
                    r2.Ok = true;
                    var parts = new string[12];
                    for (int i = 0; i < parts.Length; i++) parts[i] = "这是一段用来测试窗口高度自适应的很长很长的译文。";
                    r2.Text = string.Join("", parts);
                    f.ApplyResult(r2);
                    w(string.Format("  长内容 -> 窗口 {0} x {1}（应被屏幕上限制约，内部滚动）", f.Width, f.Height));

                    // 朗读按钮的开关行为：点亮=读，再点=停（变暗），再点=从头读。
                    // 放在最后做，因为下面会 Close() 掉窗口。
                    w("");
                    w("  ---- 朗读按钮开关 ----");
                    w("    初始        : " + (f.SpeakerOn ? "亮" : "暗"));
                    f.ClickSpeaker();
                    System.Threading.Thread.Sleep(500);
                    w("    第1次点     : " + (f.SpeakerOn ? "亮 ✅ 开始读" : "暗 ❌ 应该亮"));
                    f.ClickSpeaker();
                    System.Threading.Thread.Sleep(400);
                    w("    第2次点     : " + (f.SpeakerOn ? "亮 ❌ 应该暗" : "暗 ✅ 已停止"));
                    f.ClickSpeaker();
                    System.Threading.Thread.Sleep(500);
                    w("    第3次点     : " + (f.SpeakerOn ? "亮 ✅ 从头再读" : "暗 ❌ 应该亮"));

                    // 关键用例：等合成完成、音频真正在放的时候点停止。
                    // 必须立刻返回，绝不能卡住界面（以前 MCI 用 play...wait，锁被占着，
                    // 这时候点停止会让界面冻住，而且声音也停不下来）。
                    System.Threading.Thread.Sleep(5000);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    f.ClickSpeaker();
                    sw.Stop();
                    w("    播放中途停止: 耗时 " + sw.ElapsedMilliseconds + " ms，按钮"
                      + (f.SpeakerOn ? "亮 ❌" : "暗 ✅")
                      + (sw.ElapsedMilliseconds < 500 ? "，没卡界面 ✅" : "，卡住了 ❌"));

                    // 窗口一关，声音必须同步停
                    f.ClickSpeaker();
                    System.Threading.Thread.Sleep(5000);
                    w("    关窗前      : 按钮" + (f.SpeakerOn ? "亮" : "暗") + " / 声音" + (Speech.IsSpeaking ? "在读" : "停了"));
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    f.Close();
                    sw2.Stop();
                    System.Threading.Thread.Sleep(300);
                    w("    关窗后      : 按钮" + (f.SpeakerOn ? "亮" : "暗") + " / 声音"
                      + (Speech.IsSpeaking ? "还在读 ❌" : "已停止 ✅")
                      + "，关窗耗时 " + sw2.ElapsedMilliseconds + " ms");
                }

                // ---- 输入模式：Ctrl+D 没取到词时弹的那个窗口 ----
                w("");
                w("  ---- 输入模式窗口 ----");
                using (var fi = new ResultForm("", cfg, true, true))
                {
                    string got = null;
                    fi.InputSubmitted += delegate(string t) { got = t; };
                    fi.SuppressAutoPlacement = true;
                    fi.StartPosition = FormStartPosition.Manual;
                    fi.Location = new Point(-4000, -4000);
                    fi.Show();
                    Application.DoEvents();

                    w("    原文区可编辑: " + (fi.SourceEditable ? "是 ✅" : "否 ❌"));
                    w("    空窗口尺寸  : " + fi.Width + " x " + fi.Height);
                    w("    未输入时不显示翻译中: " + (fi.IsLoadingNow ? "❌" : "是 ✅"));

                    const string typed = "Hello, this is typed by hand, and this sentence is deliberately long so that it has to wrap onto more than one line inside the source box.";
                    fi.SimulateTyping(typed);
                    Application.DoEvents();
                    w("    打字后尺寸  : " + fi.Width + " x " + fi.Height + "（应比空窗口高）");

                    fi.SimulateEnter();
                    w("    回车回调    : " + (got == typed ? "收到文字 ✅" : "没收对 ❌ (" + got + ")"));
                    w("    进入翻译中  : " + (fi.IsLoadingNow ? "是 ✅" : "否 ❌"));

                    var ri = new TranslateResult();
                    ri.Ok = true; ri.Engine = "test";
                    ri.Text = "你好，这是手动输入的译文。这段话故意写得很长，用来验证窗口会不会按内容自动长高，"
                              + "如果布局算错了，这里就会显示不全或者留一大片空白。再加一句凑够几行，"
                              + "确保高度确实跟着内容变化，而不是一个固定的尺寸。";
                    fi.ApplyResult(ri);
                    w("    出结果后尺寸: " + fi.Width + " x " + fi.Height + "（应比打字时更高）");
                    w("    可朗读的文本: " + (fi.BestSpeakText() == typed ? "原文 ✅" : "不对 ❌"));
                    w("    原文仍可编辑: " + (fi.SourceEditable ? "是 ✅（可改了再回车重译）" : "否"));
                }

                // ---- 复现用例：长音频播放中途关窗（用户报的 bug）----
                w("");
                w("  ---- 长音频播放中途关窗 ----");
                string longEn = string.Join(" ", new string[] {
                    "Latest from our changelog. Five hours ago. Code scanning autofix is now generally available.",
                    "This release also brings a redesigned settings page, faster search, and a number of bug fixes.",
                    "We have improved performance across the board and reduced memory usage significantly."
                });
                using (var f2 = new ResultForm(longEn, cfg, true))
                {
                    var r3 = new TranslateResult();
                    r3.Ok = true; r3.Text = "译文占位。"; r3.Engine = "Google";
                    f2.ApplyResult(r3);
                    f2.SuppressAutoPlacement = true;
                    f2.StartPosition = FormStartPosition.Manual;
                    f2.Location = new Point(-4000, -4000);
                    f2.Show();
                    Application.DoEvents();

                    w("    原文 " + longEn.Length + " 字，点朗读...");
                    f2.ClickSpeaker();
                    System.Threading.Thread.Sleep(8000);   // 等合成完并真正开始播放
                    w("    播放中      : " + (Speech.IsSpeaking ? "是 ✅" : "否 ❌（没开始播）"));

                    var sw3 = System.Diagnostics.Stopwatch.StartNew();
                    f2.Close();                            // 相当于点窗口外自动关
                    sw3.Stop();
                    System.Threading.Thread.Sleep(2000);

                    w("    关窗耗时    : " + sw3.ElapsedMilliseconds + " ms");
                    w("    关窗后声音  : " + (Speech.IsSpeaking ? "还在读 ❌" : "已停止 ✅"));

                    int left = 0;
                    try { left = Directory.GetFiles(AppPaths.TempDir, "tts_*.mp3").Length; } catch { }
                    w("    残留音频文件: " + left + " 个" + (left == 0 ? " ✅（设备已松手）" : " ❌（MCI 还占着文件）"));
                }

                // ---- 复现用例：刚停掉长音频，立刻再读一次 ----
                // 之前固定文件名时，这里会因为文件被占而写不进去，于是退回系统语音，
                // 而且之后一路都是机械音。现在应该还能用 Edge。
                w("");
                w("  ---- 停掉长音频后立刻再读（验证不会变机械音）----");
                using (var f3 = new ResultForm(longEn, cfg, true))
                {
                    var r4 = new TranslateResult();
                    r4.Ok = true; r4.Text = "译文占位。"; r4.Engine = "Google";
                    f3.ApplyResult(r4);
                    f3.SuppressAutoPlacement = true;
                    f3.StartPosition = FormStartPosition.Manual;
                    f3.Location = new Point(-4000, -4000);
                    f3.Show();
                    Application.DoEvents();

                    f3.ClickSpeaker();
                    Log.ClearLastWarning();
                    System.Threading.Thread.Sleep(7000);
                    string used = Speech.LastEngineUsed;
                    w("    这次用的引擎: " + (used == "edge" ? "Edge 神经网络 ✅" : "系统语音(机械音) ❌"));
                    var last = Log.LastWarning;
                    if (!string.IsNullOrEmpty(last)) w("    期间的警告  : " + last);
                }
            }
            catch (Exception ex) { w("  窗口测试失败: " + ex.Message); }

            // 环境
            w("");
            w("---------- 环境 ----------");
            w("进程位数: " + (IntPtr.Size == 8 ? "64 位" : "32 位"));
            w(".NET 版本: " + Environment.Version);
            w("系统代理: " + (System.Net.WebRequest.DefaultWebProxy == null ? "(无)" : "已配置"));

            w("");
            w("=== 自检结束 ===");

            // 自检产生的图片（样本图、预览图）不要留在磁盘上。
            // 文字报告（selftest.txt / translate.txt）是自检的输出，留着给人看，
            // 下次正常启动时的临时目录清扫会把它们带走。
            try
            {
                foreach (string f in Directory.GetFiles(AppPaths.TempDir, "*.png"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }

            try
            {
                File.WriteAllText(Path.Combine(AppPaths.TempDir, "selftest.txt"),
                                  sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>命令行翻译：SnipTranslate.exe --translate "hello"</summary>
        public static void TranslateOnce(string text)
        {
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }

            var sb = new StringBuilder();
            AppConfig cfg = AppConfig.Load();
            TranslateResult r = Translators.Translate(text, cfg);

            sb.AppendLine("原文: " + text);
            if (r.Ok)
            {
                sb.AppendLine("译文: " + r.Text);
                sb.AppendLine("来源: " + r.Engine);
                if (r.PhoneticUS.Length > 0) sb.AppendLine("美式音标: /" + r.PhoneticUS + "/");
                foreach (string d in r.Definitions) sb.AppendLine("  · " + d);
            }
            else sb.AppendLine("失败: " + r.Error);

            try { Console.WriteLine(sb.ToString()); } catch { }
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.TempDir, "translate.txt"),
                                  sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static Bitmap MakeSample()
        {
            var bmp = new Bitmap(1000, 300);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var f1 = new Font("Segoe UI", 22))
                using (var f2 = new Font("Segoe UI", 16))
                using (var f3 = new Font("Segoe UI", 11))
                using (var br = new SolidBrush(Color.Black))
                {
                    g.DrawString("The quick brown fox jumps over the lazy dog.", f1, br, 20, 20);
                    g.DrawString("Translation should be accurate and fast.", f1, br, 20, 70);
                    g.DrawString("Click the button below to continue.", f2, br, 20, 130);
                    g.DrawString("Error 404: file not found.", f2, br, 20, 170);
                    g.DrawString("This is a smaller line to test small-font recognition.", f3, br, 20, 220);
                }
            }
            return bmp;
        }
    }
}
