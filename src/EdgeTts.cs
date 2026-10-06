using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SnipTranslate
{
    /// <summary>
    /// Edge 的神经网络语音（就是 Edge 浏览器「大声朗读」用的那套）。
    /// 公开接口、不需要 API Key，但**需要联网**。
    ///
    /// 之所以要它：Windows 自带的 SAPI 语音（Zira 之类）是拼接式合成，听着很机械；
    /// Edge 这套是神经网络的，`en-US-AvaNeural` 这种几乎听不出是合成音。
    ///
    /// 两个踩过的坑：
    ///   1. 服务端**要求带 Edge 的 User-Agent**，不带一律 403。而 .NET 自带的
    ///      ClientWebSocket 不允许设 User-Agent（受限标头），所以这里自己实现了
    ///      WebSocket 握手（见 RawWebSocket）。实测带 UA 用 curl 请求返回 101，不带 403。
    ///   2. 2024 年起必须带 `Sec-MS-GEC` 参数（时间窗 + 令牌的 SHA256），否则也是 403。
    ///
    /// 音频要求 `riff-24khz-16bit-mono-pcm` 格式，拿回来就是标准 WAV，
    /// 可以直接用 .NET 自带的 SoundPlayer 播 —— 不需要任何 MP3 解码库。
    /// </summary>
    internal static class EdgeTts
    {
        private const string TrustedToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        private const string Host = "speech.platform.bing.com";

        // 版本串要跟真实 Edge 对得上，官方 edge-tts 当前用的是这个
        private const string ChromiumFullVersion = "143.0.3650.75";
        private const string ChromiumMajor = "143";

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/" + ChromiumMajor + ".0.0.0 Safari/537.36 "
            + "Edg/" + ChromiumMajor + ".0.0.0";

        private const string ConfigJson =
            "{\"context\":{\"synthesis\":{\"audio\":{"
            + "\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},"
            + "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";

        private static readonly object SynthGate = new object();
        private static RawWebSocket _shared;   // 复用连接，省掉每次 1.2 秒的握手

        /// <summary>合成一段语音，返回 WAV 字节；失败抛异常。调用方负责放到后台线程上跑。</summary>
        public static byte[] Synthesize(string text, string voice, int ratePercent, int timeoutMs)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (string.IsNullOrEmpty(voice)) voice = "en-US-AvaNeural";

            // 串行化：同一个连接不能两个线程同时用
            lock (SynthGate)
            {
                // 复用上次的连接。握手要 1.2 秒，占了总延迟的一大半，能省则省。
                RawWebSocket ws = _shared;
                if (ws != null && ws.Connected)
                {
                    try
                    {
                        byte[] a = DoTurn(ws, text, voice, ratePercent, timeoutMs, true);
                        if (a != null) return a;
                    }
                    catch (Exception ex)
                    {
                        Log.Info("复用连接失败（" + ex.Message + "），重连一次");
                    }
                    DropShared();
                }

                var fresh = new RawWebSocket();
                try
                {
                    byte[] b = DoTurn(fresh, text, voice, ratePercent, timeoutMs, false);
                    _shared = fresh;      // 留着给下次用
                    return b;
                }
                catch
                {
                    try { fresh.Dispose(); } catch { }
                    DropShared();
                    throw;
                }
            }
        }

        private static void DropShared()
        {
            RawWebSocket old = _shared;
            _shared = null;
            if (old != null) { try { old.Dispose(); } catch { } }
        }

        /// <summary>关掉复用的连接（退出时调）。</summary>
        public static void CloseShared()
        {
            lock (SynthGate) { DropShared(); }
        }

        private static byte[] DoTurn(RawWebSocket ws, string text, string voice,
                                     int ratePercent, int timeoutMs, bool reused)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            if (!reused)
            {
                string query = "/consumer/speech/synthesize/readaloud/edge/v1"
                               + "?TrustedClientToken=" + TrustedToken
                               + "&ConnectionId=" + Guid.NewGuid().ToString("N")
                               + "&Sec-MS-GEC=" + SecMsGec()
                               + "&Sec-MS-GEC-Version=1-" + ChromiumFullVersion;

                var headers = new Dictionary<string, string>();
                headers["Pragma"] = "no-cache";
                headers["Cache-Control"] = "no-cache";
                headers["Origin"] = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";
                headers["Accept-Language"] = "en-US,en;q=0.9";
                headers["User-Agent"] = UserAgent;          // 关键：少了这个就 403
                headers["Cookie"] = "muid=" + RandomHex(16) + ";";

                ws.Connect(Host, query, headers, timeoutMs);
            }

            long tConnect = clock.ElapsedMilliseconds;

            SendText(ws, "speech.config", ConfigJson);

            string rate = (ratePercent >= 0 ? "+" : "") + ratePercent + "%";
            string ssml =
                "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>"
                + "<voice name='" + voice + "'>"
                + "<prosody pitch='+0Hz' rate='" + rate + "' volume='+0%'>"
                + EscapeXml(text)
                + "</prosody></voice></speak>";
            SendText(ws, "ssml", ssml);
            long tSend = clock.ElapsedMilliseconds;

            var audio = new MemoryStream();
            var serverMsgs = new List<string>();
            long tFirstAudio = -1;
            while (true)
            {
                bool isText;
                byte[] msg = ws.ReceiveMessage(out isText, timeoutMs);
                if (msg == null) break;                    // 连接关闭

                if (isText)
                {
                    string s = Encoding.UTF8.GetString(msg);
                    if (serverMsgs.Count < 6)
                        serverMsgs.Add(s.Length > 260 ? s.Substring(0, 260) : s);
                    if (s.IndexOf("Path:turn.end", StringComparison.Ordinal) >= 0) break;
                    continue;
                }

                // 二进制帧：前 2 字节是大端序的头长度，接着是头，然后是音频
                if (msg.Length < 2) continue;
                int headerLen = (msg[0] << 8) | msg[1];
                int start = 2 + headerLen;
                if (start < msg.Length)
                {
                    if (tFirstAudio < 0) tFirstAudio = clock.ElapsedMilliseconds;
                    audio.Write(msg, start, msg.Length - start);
                }
            }

            Log.Info(string.Format(
                "Edge TTS {0}：连接 {1}ms / 首包 {2}ms / 全部 {3}ms（{4} 字）",
                reused ? "复用连接" : "新建连接",
                tConnect, tFirstAudio < 0 ? -1 : tFirstAudio, clock.ElapsedMilliseconds, text.Length));

            byte[] wav = audio.ToArray();
            audio.Dispose();

            if (wav.Length < 128)
                throw new Exception("没收到音频数据（" + wav.Length + " 字节）。服务端消息: "
                                    + string.Join(" ][ ", serverMsgs.ToArray()));
            return wav;
        }

        private static void SendText(RawWebSocket ws, string path, string body)
        {
            string msg = "X-RequestId:" + Guid.NewGuid().ToString("N") + "\r\n"
                         + "Content-Type:" + (path == "ssml"
                               ? "application/ssml+xml"
                               : "application/json; charset=utf-8") + "\r\n"
                         + "X-Timestamp:" + JsDate() + (path == "ssml" ? "Z" : "") + "\r\n"
                         + "Path:" + path + "\r\n\r\n"
                         + body;
            ws.SendText(msg);
        }

        /// <summary>Edge 用的是 JavaScript 风格的日期串。</summary>
        private static string JsDate()
        {
            string[] dow = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            string[] mon = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            DateTime u = DateTime.UtcNow;
            return string.Format("{0} {1} {2:D2} {3:D4} {4:D2}:{5:D2}:{6:D2} GMT+0000 (Coordinated Universal Time)",
                dow[(int)u.DayOfWeek], mon[u.Month - 1], u.Day, u.Year, u.Hour, u.Minute, u.Second);
        }

        /// <summary>
        /// 反滥用校验：不带这个参数握手会被拒（403）。
        /// 算法：把「以 1601-01-01 起算、按 5 分钟取整的 100 纳秒数」拼上 TrustedClientToken 做 SHA256，大写十六进制。
        /// </summary>
        private static string SecMsGec()
        {
            const long WinEpochSeconds = 11644473600L;   // 1601-01-01 → 1970-01-01

            long ticks = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds
                         + WinEpochSeconds;
            ticks -= ticks % 300;                        // 按 5 分钟向下取整
            ticks *= 10000000L;                          // 秒 → 100 纳秒

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(ticks.ToString() + TrustedToken));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("X2"));
                return sb.ToString();
            }
        }

        private static string RandomHex(int bytes)
        {
            byte[] buf = new byte[bytes];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(buf);
            var sb = new StringBuilder(bytes * 2);
            foreach (byte b in buf) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        private static string EscapeXml(string s)
        {
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
        }

        /// <summary>拉一份可用语音清单（诊断用，走普通 HTTPS）。</summary>
        public static string ListVoices(int timeoutMs)
        {
            string url = "https://" + Host + "/consumer/speech/synthesize/readaloud/voices/list"
                         + "?trustedclienttoken=" + TrustedToken;
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = timeoutMs;
            req.Proxy = null;   // 直连。很多代理软件对这类接口支持不好，走代理反而连不上
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }
    }
}
