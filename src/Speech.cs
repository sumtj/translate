using System;
using System.Collections.Generic;
using System.Speech.Synthesis;
using System.Threading;

namespace SnipTranslate
{
    /// <summary>
    /// 发音。两个引擎，按配置选择：
    ///
    ///   edge —— Edge 的神经网络语音（`en-US-AvaNeural` 之类），**最自然、有人味**，但需要联网。
    ///   sapi —— Windows 自带的 SAPI 语音（Zira），拼接式合成，听着机械，但**完全离线**。
    ///
    /// 默认 auto：先用 edge，合成失败（断网、接口变动）就自动退回 sapi，保证永远读得出声。
    /// </summary>
    internal static class Speech
    {
        private static readonly object Gate = new object();

        // --- SAPI ---
        private static SpeechSynthesizer _syn;
        private static string _sapiVoice = "(未找到英语语音)";

        // --- 通用状态 ---
        private static string _engine = "auto";
        private static string _edgeVoice = "en-US-AvaNeural";
        private static int _rate;
        private static string _effective = "(未初始化)";

        // --- 播放 ---
        private static int _speakToken;     // 每次「开始朗读」自增；Stop 也自增，用来取消在途的合成
        private static int _playToken;      // 每次播放自增，用来丢弃过期的播放
        private static string _cacheKey;
        private static byte[] _cacheWav;

        // 预合成状态
        private static string _prefetchKey;
        private static bool _prefetchRunning;
        private static DateTime _requestedAt;

        /// <summary>朗读开始/结束时通知界面（用来点亮朗读按钮）。true = 正在朗读。</summary>
        public static event Action<bool> SpeakingChanged;

        private static volatile bool _speaking;

        private static volatile string _lastUsed = "";

        /// <summary>上一次朗读实际用的是哪个引擎（诊断用）："edge" 或 "sapi"。</summary>
        public static string LastEngineUsed { get { return _lastUsed; } }

        /// <summary>当前是否正在朗读。</summary>
        public static bool IsSpeaking { get { return _speaking; } }

        private static void RaiseSpeaking(bool speaking)
        {
            _speaking = speaking;
            Action<bool> h = SpeakingChanged;
            if (h == null) return;
            try { h(speaking); } catch { }
        }

        /// <summary>当前实际生效的发音。</summary>
        public static string VoiceName { get { return _effective; } }

        public static string EngineName
        {
            get
            {
                if (_engine == "sapi") return "系统语音(离线)";
                if (_engine == "edge") return "Edge 神经网络";
                return "自动（优先 Edge，断网退回系统语音）";
            }
        }

        // ==================== 初始化 ====================

        public static void Init(int rate, string engine, string edgeVoice)
        {
            lock (Gate)
            {
                _rate = rate;
                _engine = string.IsNullOrEmpty(engine) ? "auto" : engine.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(edgeVoice)) _edgeVoice = edgeVoice.Trim();

                InitSapi(rate);

                if (_engine == "sapi") _effective = _sapiVoice;
                else _effective = _edgeVoice + "（断网时用 " + _sapiVoice + "）";

                Log.Info("发音引擎: " + EngineName + " | 语音: " + _effective);
            }
        }

        private static void InitSapi(int rate)
        {
            if (_syn != null) { try { _syn.Rate = rate; } catch { } return; }

            try
            {
                _syn = new SpeechSynthesizer();
                _syn.SetOutputToDefaultAudioDevice();
                _syn.Rate = Math.Max(-10, Math.Min(10, rate));

                InstalledVoice fallback = null;
                foreach (InstalledVoice v in _syn.GetInstalledVoices())
                {
                    if (!v.Enabled) continue;
                    VoiceInfo info = v.VoiceInfo;
                    string culture = info.Culture == null ? "" : info.Culture.Name;

                    if (culture.StartsWith("en-US", StringComparison.OrdinalIgnoreCase))
                    {
                        _syn.SelectVoice(info.Name);
                        _sapiVoice = info.Name;
                        return;
                    }
                    if (fallback == null && culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                        fallback = v;
                }

                if (fallback != null)
                {
                    _syn.SelectVoice(fallback.VoiceInfo.Name);
                    _sapiVoice = fallback.VoiceInfo.Name;
                }
                else Log.Warn("系统里没有英语语音");
            }
            catch (Exception ex)
            {
                Log.Error("初始化系统语音失败", ex);
                _syn = null;
            }
        }

        public static void SetRate(int rate)
        {
            lock (Gate)
            {
                _rate = Math.Max(-10, Math.Min(10, rate));
                if (_syn != null) { try { _syn.Rate = rate; } catch { } }
            }
        }

        // ==================== 朗读 ====================

        public static void Speak(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = text.Trim();
            if (text.Length == 0) return;
            if (text.Length > 2000) text = text.Substring(0, 2000);

            string engine;
            int rate;
            string voice;
            lock (Gate)
            {
                engine = _engine;
                rate = _rate;
                voice = _edgeVoice;
            }

            if (engine == "sapi") { SpeakViaSapi(text); return; }

            // Edge 要联网，放到后台线程去合成，别卡界面。
            // 带上令牌：合成要 1~2 秒，这期间用户可能已经点了「停止」，
            // 合成完必须先查令牌，否则会「按了停止又自己响起来」。
            string t = text;
            int r = rate;
            string v = voice;
            int token;
            lock (Gate)
            {
                _speakToken++;
                token = _speakToken;
                _requestedAt = DateTime.UtcNow;   // 用来量「点击到出声」到底多久
            }
            ThreadPool.QueueUserWorkItem(delegate { SpeakViaEdge(t, v, r, token); });
        }

        /// <summary>这次朗读是否已经被取消（用户点了停止，或又发起了新一轮朗读）。</summary>
        private static bool IsCancelled(int token)
        {
            lock (Gate) { return token != _speakToken; }
        }

        /// <summary>
        /// 预合成：翻译结果一出来就在后台把音频拉好。
        /// 点朗读时就不用等那 1~2 秒了，直接出声。
        /// </summary>
        public static void Prefetch(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = text.Trim();
            if (text.Length == 0) return;
            if (text.Length > 2000) text = text.Substring(0, 2000);

            string voice;
            int rate;
            string key;

            lock (Gate)
            {
                if (_engine == "sapi") return;   // 系统语音本来就没什么延迟，不用预生成
                voice = _edgeVoice;
                rate = _rate;
                key = voice + "|" + rate + "|" + text;

                if (_cacheKey == key && _cacheWav != null) return;   // 已经有了
                // 一次只跑一个预合成。否则连续翻译会堆一堆请求，
                // 线程池被阻塞的活儿占满，只好不停注入新线程（句柄跟着涨）。
                if (_prefetchRunning) return;

                _prefetchRunning = true;
                _prefetchKey = key;
            }

            string t = text;
            string v = voice;
            int r = rate;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[] a = EdgeTts.Synthesize(t, v, r * 10, 15000);
                    sw.Stop();
                    lock (Gate) { _cacheKey = key; _cacheWav = a; }
                    Log.Info("预合成完成 " + t.Length + " 字，耗时 " + sw.ElapsedMilliseconds + "ms（点朗读可立刻出声）");
                }
                catch (Exception ex)
                {
                    Log.Warn("预合成失败（不影响点朗读，届时现拉）: " + ex.Message);
                }
                finally
                {
                    // 用 Monitor 而不是 ManualResetEventSlim：
                    // 后者 Wait() 时会创建一个内核事件对象，忘了 Dispose 就是句柄泄漏。
                    // Monitor 走对象同步块，不占内核句柄。
                    lock (Gate)
                    {
                        _prefetchRunning = false;
                        Monitor.PulseAll(Gate);
                    }
                }
            });
        }

        /// <summary>如果正好在预合成同一段，等它完成，避免重复请求。</summary>
        private static void WaitForPrefetch(string key)
        {
            lock (Gate)
            {
                if (!_prefetchRunning || _prefetchKey != key) return;
                Monitor.Wait(Gate, 15000);
            }
        }

        private static void SpeakViaEdge(string text, string voice, int rate, int token)
        {
            // 合成要 1~2 秒，先把按钮点亮，否则用户会以为点了没反应
            RaiseSpeaking(true);
            try
            {
                byte[] wav = null;
                string key = voice + "|" + rate + "|" + text;

                lock (Gate)
                {
                    if (_cacheKey == key && _cacheWav != null) wav = _cacheWav;
                }

                if (wav == null)
                {
                    // 可能刚好在预合成这一句 —— 那就等它，别再拉一次
                    WaitForPrefetch(key);
                    lock (Gate)
                    {
                        if (_cacheKey == key && _cacheWav != null) wav = _cacheWav;
                    }
                }

                if (wav == null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    wav = EdgeTts.Synthesize(text, voice, rate * 10, 15000);
                    sw.Stop();
                    Log.Info("Edge TTS 现拉 " + text.Length + " 字，耗时 " + sw.ElapsedMilliseconds + "ms");

                    lock (Gate) { _cacheKey = key; _cacheWav = wav; }
                }
                else
                {
                    Log.Info("朗读命中缓存，立刻出声");
                }

                if (IsCancelled(token)) return;   // 合成期间被停掉了，别再出声
                _lastUsed = "edge";
                Log.Info("点击到出声 " + (int)(DateTime.UtcNow - _requestedAt).TotalMilliseconds + " ms");
                PlayAudio(wav, token);
            }
            catch (Exception ex)
            {
                if (IsCancelled(token)) return;   // 用户已经喊停了，就别再退回系统语音接着念
                Log.Warn("Edge TTS 不可用（" + ex.Message + "），改用系统语音");
                SpeakViaSapi(text);
            }
        }

        /// <summary>
        /// 播放音频。失败会往外抛，好让调用方退回系统语音 —— 绝不能悄悄没声。
        /// </summary>
        private static void PlayAudio(byte[] data, int speakToken)
        {
            if (IsCancelled(speakToken)) return;

            int token;
            lock (Gate)
            {
                _playToken++;
                token = _playToken;
            }
            AudioPlayer.Stop();   // 打断上一次

            RaiseSpeaking(true);

            // MCI 必须跑在 STA 线程上：线程池是 MTA，直接调会报
            // MCIERR_CANNOT_LOAD_DRIVER(266)，驱动加载不起来。
            Exception failure = null;
            var t = new Thread(new ThreadStart(delegate
            {
                try { AudioPlayer.PlayAndWait(data); }   // 阻塞到播完
                catch (Exception ex) { failure = ex; }
            }));
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();   // Join 之后读 failure 是安全的

            bool stillCurrent;
            lock (Gate) { stillCurrent = (token == _playToken); }
            if (stillCurrent) RaiseSpeaking(false);

            if (failure != null) throw new Exception(failure.Message, failure);
        }

        private static void SpeakViaSapi(string text)
        {
            lock (Gate)
            {
                if (_syn == null) { Log.Warn("系统语音也没初始化，读不了"); return; }
                try
                {
                    _syn.SpeakAsyncCancelAll();
                    _lastUsed = "sapi";
                    RaiseSpeaking(true);
                    _syn.SpeakAsync(text);
                }
                catch (Exception ex)
                {
                    Log.Error("系统语音朗读失败", ex);
                    RaiseSpeaking(false);
                }
            }
        }

        /// <summary>同步朗读，读完才返回。给命令行 <c>--say</c> 用。</summary>
        public static void SpeakAndWait(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = text.Trim();
            if (text.Length == 0) return;
            if (text.Length > 2000) text = text.Substring(0, 2000);

            string engine;
            int rate;
            string voice;
            lock (Gate) { engine = _engine; rate = _rate; voice = _edgeVoice; }

            if (engine != "sapi")
            {
                try
                {
                    byte[] wav = EdgeTts.Synthesize(text, voice, rate * 10, 15000);
                    int token;
                    lock (Gate) { _speakToken++; token = _speakToken; }
                    PlayAudio(wav, token);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("Edge TTS 不可用（" + ex.Message + "），改用系统语音");
                }
            }

            lock (Gate)
            {
                if (_syn == null) return;
                try
                {
                    _syn.SpeakAsyncCancelAll();
                    _syn.Speak(text);   // 同步，读完才返回
                }
                catch (Exception ex) { Log.Error("朗读失败", ex); }
            }
        }

        public static void Stop()
        {
            Log.Info("Speech.Stop 被调用（当前" + (_speaking ? "正在读" : "没在读") + "）");
            lock (Gate)
            {
                _speakToken++;   // 让在途的合成作废，别等会儿又自己响起来
                _playToken++;
                AudioPlayer.Stop();
                try { if (_syn != null) _syn.SpeakAsyncCancelAll(); } catch { }
            }
            RaiseSpeaking(false);
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                _playToken++;
                AudioPlayer.Stop();
                if (_syn != null)
                {
                    try { _syn.SpeakAsyncCancelAll(); _syn.Dispose(); } catch { }
                    _syn = null;
                }
            }
            try { EdgeTts.CloseShared(); } catch { }
        }

        // ==================== 诊断 ====================

        /// <summary>系统里可用的 SAPI 语音。</summary>
        public static List<string> ListSapiVoices()
        {
            var list = new List<string>();
            try
            {
                using (var s = new SpeechSynthesizer())
                {
                    foreach (InstalledVoice v in s.GetInstalledVoices())
                    {
                        VoiceInfo i = v.VoiceInfo;
                        list.Add(i.Name + "  [" + (i.Culture == null ? "?" : i.Culture.Name) + "]");
                    }
                }
            }
            catch (Exception ex) { Log.Warn("枚举系统语音失败: " + ex.Message); }
            return list;
        }

        /// <summary>
        /// Edge 里可用的英文女声。
        ///
        /// 这里用内置清单而不是联网拉取：拉取走的是 HttpWebRequest，在本机会被服务端 RST
        /// （同样的请求在 PowerShell 里却能成，原因没查明）；而语音列表本来就是稳定的，
        /// 内置一份既可靠又不依赖网络。清单来自官方 voices/list 接口，实测这些名字都能合成。
        /// </summary>
        public static List<string> ListEdgeVoices(int timeoutMs)
        {
            return new List<string>
            {
                "en-US-AvaNeural          最有亲和力（Expressive / Caring / Pleasant / Friendly）",
                "en-US-AvaMultilingualNeural  同上，多语言版",
                "en-US-EmmaNeural         活泼（Cheerful / Clear / Conversational）",
                "en-US-EmmaMultilingualNeural  同上，多语言版",
                "en-US-JennyNeural        温柔（Friendly / Considerate / Comfort）",
                "en-US-MichelleNeural     成熟亲切（Friendly / Pleasant）",
                "en-US-AriaNeural         自信（Positive / Confident）",
                "en-US-AnaNeural          童声（Cute / Cartoon）",
            };
        }

        /// <summary>联网拉取 Edge 语音全量清单。本机环境会被 RST，失败时请用内置清单。</summary>
        public static List<string> FetchEdgeVoices(int timeoutMs)
        {
            var list = new List<string>();
            try
            {
                string json = EdgeTts.ListVoices(timeoutMs);
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                object[] arr = ser.DeserializeObject(json) as object[];
                if (arr == null) return list;

                foreach (object o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    object loc, gen, name;
                    if (!d.TryGetValue("Locale", out loc) || !d.TryGetValue("Gender", out gen)) continue;
                    if (Convert.ToString(loc) != "en-US" || Convert.ToString(gen) != "Female") continue;
                    d.TryGetValue("ShortName", out name);
                    list.Add(Convert.ToString(name));
                }
                list.Sort();
            }
            catch (Exception ex)
            {
                var sb = new System.Text.StringBuilder(ex.Message);
                Exception e = ex.InnerException;
                while (e != null) { sb.Append("  <-  ").Append(e.Message); e = e.InnerException; }
                Log.Warn("拉取 Edge 语音列表失败（用内置清单即可）: " + sb);
            }
            return list;
        }
    }
}
