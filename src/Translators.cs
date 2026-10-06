using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace SnipTranslate
{
    internal class TranslateResult
    {
        public bool Ok;
        public string Text = "";
        public string Engine = "";
        public string Error = "";
        public bool IsWord;
        public string PhoneticUS = "";
        public string PhoneticUK = "";
        public List<string> Definitions = new List<string>();
    }

    /// <summary>
    /// 多翻译源，按配置顺序依次尝试，第一个成功就用它。
    /// 免费源：Google（需代理）、有道（可直连）、MyMemory（可直连）。
    /// </summary>
    internal static class Translators
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>某个源刚失败过就先冷处理，避免每次都要等一遍超时。</summary>
        private static readonly Dictionary<string, DateTime> Cooldown = new Dictionary<string, DateTime>();
        private const int CooldownSeconds = 120;

        static Translators()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }
        }

        private static bool InCooldown(string id)
        {
            DateTime until;
            if (!Cooldown.TryGetValue(id, out until)) return false;
            if (DateTime.UtcNow >= until) { Cooldown.Remove(id); return false; }
            return true;
        }

        private static void SetCooldown(string id) { Cooldown[id] = DateTime.UtcNow.AddSeconds(CooldownSeconds); }

        // ==================== 对外入口 ====================

        public static TranslateResult Translate(string text, AppConfig cfg)
        {
            var res = new TranslateResult();
            text = (text ?? "").Trim();

            if (text.Length == 0) { res.Error = "没有可翻译的内容"; return res; }
            if (text.Length > 5000) text = text.Substring(0, 5000);

            bool zhToEn = IsMostlyChinese(text);
            res.IsWord = !zhToEn && LooksLikeSingleWord(text);

            if (cfg.ShowPhonetic && res.IsWord)
            {
                try { FillDictionary(text, res, cfg.TimeoutMs); }
                catch (Exception ex) { Log.Warn("词典查询失败: " + ex.Message); }
            }

            var errors = new List<string>();
            string[] order = (cfg.EngineOrder ?? "").Split(',');

            foreach (string raw in order)
            {
                string id = raw.Trim().ToLowerInvariant();
                if (id.Length == 0) continue;

                if (InCooldown(id)) { errors.Add(EngineName(id) + "：刚失败过，暂时跳过"); continue; }

                string outText = null;
                string err = null;
                bool ok = false;

                try
                {
                    switch (id)
                    {
                        case "youdao": ok = TryYoudao(text, zhToEn, cfg.TimeoutMs, out outText, out err); break;
                        case "google": ok = TryGoogle(text, zhToEn, cfg.TimeoutMs, out outText, out err); break;
                        case "mymemory": ok = TryMyMemory(text, zhToEn, cfg.TimeoutMs, out outText, out err); break;
                        default: continue;
                    }
                }
                catch (Exception ex) { ok = false; err = ex.Message; }

                if (ok && !string.IsNullOrEmpty(outText))
                {
                    res.Ok = true;
                    res.Text = outText.Trim();
                    res.Engine = EngineName(id);
                    Cooldown.Remove(id);
                    Log.Info("翻译成功 via " + res.Engine + "（" + text.Length + " 字）");
                    return res;
                }

                SetCooldown(id);
                errors.Add(EngineName(id) + "：" + (err ?? "无结果"));
                Log.Warn("翻译源失败 " + id + " : " + err + "（" + CooldownSeconds + " 秒内不再尝试）");
            }

            res.Error = "所有翻译源都失败了：\n" + string.Join("\n", errors.ToArray());
            return res;
        }

        private static string EngineName(string id)
        {
            switch (id)
            {
                case "youdao": return "有道";
                case "google": return "Google";
                case "mymemory": return "MyMemory";
                default: return id;
            }
        }

        // ==================== 各翻译源 ====================

        private static bool TryYoudao(string text, bool zhToEn, int timeout, out string result, out string error)
        {
            result = null;
            error = null;

            string to = zhToEn ? "en" : "zh-CHS";
            string url = "https://aidemo.youdao.com/trans?q=" + Uri.EscapeDataString(text) + "&from=auto&to=" + to;

            string json = Fetch(url, timeout, false);
            Dictionary<string, object> dict = AsDict(Ser.DeserializeObject(json));
            if (dict == null) { error = "返回格式异常"; return false; }

            string code = AsStr(Get(dict, "errorCode"));
            if (code != "0") { error = "errorCode=" + code; return false; }

            object[] arr = AsArr(Get(dict, "translation"));
            if (arr == null || arr.Length == 0) { error = "translation 为空"; return false; }

            var sb = new StringBuilder();
            foreach (object o in arr) sb.Append(AsStr(o));
            result = sb.ToString();

            if (result.Length == 0) { error = "结果为空"; return false; }
            return true;
        }

        private static bool TryGoogle(string text, bool zhToEn, int timeout, out string result, out string error)
        {
            result = null;
            error = null;

            string tl = zhToEn ? "en" : "zh-CN";
            string url = "https://translate.googleapis.com/translate_a/single"
                         + "?client=gtx&sl=auto&tl=" + tl + "&dt=t&q=" + Uri.EscapeDataString(text);

            string json = Fetch(url, timeout, true); // Google 在墙内必须走代理
            object[] top = AsArr(Ser.DeserializeObject(json));
            if (top == null || top.Length == 0) { error = "返回格式异常"; return false; }

            object[] segments = AsArr(top[0]);
            if (segments == null) { error = "segments 为空"; return false; }

            var sb = new StringBuilder();
            foreach (object seg in segments)
            {
                object[] parts = AsArr(seg);
                if (parts != null && parts.Length > 0) sb.Append(AsStr(parts[0]));
            }
            result = sb.ToString();

            if (result.Length == 0) { error = "结果为空"; return false; }
            return true;
        }

        private static bool TryMyMemory(string text, bool zhToEn, int timeout, out string result, out string error)
        {
            result = null;
            error = null;

            string pair = zhToEn ? "zh-CN|en" : "en|zh-CN";
            string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text)
                         + "&langpair=" + Uri.EscapeDataString(pair);

            string json = Fetch(url, timeout, false);
            Dictionary<string, object> dict = AsDict(Ser.DeserializeObject(json));
            if (dict == null) { error = "返回格式异常"; return false; }

            Dictionary<string, object> data = AsDict(Get(dict, "responseData"));
            if (data == null) { error = "responseData 为空"; return false; }

            string t = AsStr(Get(data, "translatedText"));
            if (string.IsNullOrEmpty(t)) { error = "translatedText 为空"; return false; }
            if (t.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                error = "免费额度已用尽";
                return false;
            }

            result = t;
            return true;
        }

        // ==================== 词典：美式音标 + 释义 ====================

        private static void FillDictionary(string word, TranslateResult res, int timeout)
        {
            string url = "https://dict.youdao.com/jsonapi?q=" + Uri.EscapeDataString(word);
            string json;
            try { json = Fetch(url, timeout, false); }
            catch (Exception ex) { Log.Warn("词典请求失败: " + ex.Message); return; }

            Dictionary<string, object> ec = AsDict(Get(Ser.DeserializeObject(json), "ec"));
            if (ec == null) return;

            object[] words = AsArr(Get(ec, "word"));
            if (words == null || words.Length == 0) return;

            Dictionary<string, object> w = AsDict(words[0]);
            if (w == null) return;

            res.PhoneticUS = AsStr(Get(w, "usphone")) ?? "";
            res.PhoneticUK = AsStr(Get(w, "ukphone")) ?? "";
            res.IsWord = true;

            object[] trs = AsArr(Get(w, "trs"));
            if (trs == null) return;

            foreach (object trItem in trs)
            {
                Dictionary<string, object> trObj = AsDict(trItem);
                if (trObj == null) continue;
                object[] trArr = AsArr(Get(trObj, "tr"));
                if (trArr == null) continue;

                foreach (object one in trArr)
                {
                    Dictionary<string, object> oneObj = AsDict(one);
                    if (oneObj == null) continue;
                    Dictionary<string, object> l = AsDict(Get(oneObj, "l"));
                    if (l == null) continue;
                    object[] items = AsArr(Get(l, "i"));
                    if (items == null) continue;
                    foreach (object it in items)
                    {
                        string s = AsStr(it);
                        if (!string.IsNullOrEmpty(s)) res.Definitions.Add(s.Trim());
                    }
                }
            }

            var seen = new HashSet<string>();
            var cleaned = new List<string>();
            foreach (string d in res.Definitions)
            {
                if (seen.Add(d)) cleaned.Add(d);
                if (cleaned.Count >= 4) break;
            }
            res.Definitions = cleaned;
        }

        // ==================== HTTP ====================

        /// <summary>preferProxy=true 时先用系统代理，失败再直连；反之亦然。</summary>
        private static string Fetch(string url, int timeoutMs, bool preferProxy)
        {
            int firstTimeout = Math.Min(timeoutMs, 3000);
            int secondTimeout = Math.Min(timeoutMs, 5000);

            Exception first = null;
            try { return HttpGet(url, firstTimeout, preferProxy); }
            catch (Exception ex) { first = ex; }

            try { return HttpGet(url, secondTimeout, !preferProxy); }
            catch (Exception ex) { throw new Exception("代理: " + first.Message + " | 直连: " + ex.Message); }
        }

        private static string HttpGet(string url, int timeoutMs, bool useProxy)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = UserAgent;
            req.Accept = "*/*";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Proxy = useProxy ? WebRequest.DefaultWebProxy : null;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // ==================== JSON 小工具 ====================

        private static Dictionary<string, object> AsDict(object o) { return o as Dictionary<string, object>; }
        private static object[] AsArr(object o) { return o as object[]; }

        private static string AsStr(object o)
        {
            if (o == null) return null;
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        private static object Get(object container, string key)
        {
            Dictionary<string, object> d = AsDict(container);
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v;
            return null;
        }

        // ==================== 语言判断 ====================

        public static bool IsMostlyChinese(string s)
        {
            int cjk = 0, latin = 0;
            foreach (char c in s)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
            }
            if (cjk == 0) return false;
            if (latin == 0) return true;
            return cjk >= latin;
        }

        /// <summary>大概是"一个词"：纯英文字母，1~2 个单词，长度不超过 30。</summary>
        public static bool LooksLikeSingleWord(string s)
        {
            s = s.Trim();
            if (s.Length == 0 || s.Length > 30) return false;
            int words = 0;
            bool inWord = false;
            foreach (char c in s)
            {
                if (char.IsLetter(c) || c == '\'' || c == '-')
                {
                    if (!inWord) { words++; inWord = true; }
                }
                else if (c == ' ') inWord = false;
                else return false;
            }
            return words >= 1 && words <= 2;
        }
    }
}
