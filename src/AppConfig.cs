using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SnipTranslate
{
    /// <summary>
    /// 配置。优先读写 exe 同目录的 SnipTranslate.ini（绿色便携），
    /// 目录不可写时退回 %APPDATA%\SnipTranslate\。
    /// </summary>
    internal class AppConfig
    {
        // ---- 热键 ----
        // 只有划词和截图默认占用快捷键；其余留空 = 不注册，功能仍可从托盘右键菜单调用。
        public string HotkeyGrab = "Ctrl+D";        // 划词翻译
        public string HotkeySnip = "Ctrl+S";        // 截图翻译
        public string HotkeyOcr = "";               // 截图 OCR（默认不占用快捷键）
        public string HotkeySpeak = "";             // 朗读上一次的原文（默认不占用）
        public string HotkeyInput = "";             // 输入翻译（默认不占用）

        // ---- 行为 ----
        public bool UseKeyboardHook = true;
        public bool CloseOnBlur = true;
        public bool AutoSpeak = false;
        public bool KeepClipboard = true;
        public bool StartupTip = false;

        // ---- 内容 ----
        public string EngineOrder = "google,youdao,mymemory";
        public bool ShowPhonetic = true;

        // ---- OCR / 发音 ----
        public string OcrLanguage = "auto";
        public int OcrScale = 2;
        public int SpeechRate = 0;

        // 发音引擎：auto = 优先 Edge 神经网络语音、断网退回系统语音；也可写死 edge / sapi
        public string TtsEngine = "auto";
        // Edge 语音名。年轻女声推荐：
        //   en-US-AvaNeural     —— 最有亲和力（Expressive / Caring / Pleasant / Friendly）
        //   en-US-EmmaNeural    —— 活泼（Cheerful / Conversational）
        //   en-US-JennyNeural   —— 温柔（Friendly / Considerate / Comfort）
        //   en-US-AriaNeural    —— 自信（Positive / Confident）
        public string TtsVoice = "en-US-AvaNeural";

        // 结果一出来就在后台把发音合成好，点朗读时不用等那 1~2 秒
        public bool PrefetchSpeech = true;

        // 取词时等剪贴板变化的预算（毫秒）。
        // 这个值决定「没选中文字」时按 Ctrl+D 要等多久才弹输入窗，越小越跟手。
        // 实测有选中内容时所有应用都是 25ms 内复制好，200ms 已经 8 倍余量。
        public int ClipboardWaitMs = ClipboardHelper.DefaultWaitMs;

        // ---- 窗口 ----
        public string Theme = "dark";         // dark（默认，仿 VS Code 暗色）/ light
        public int WindowWidth = 420;
        public int MaxHeightPercent = 75;
        public int TimeoutMs = 8000;

        public string ConfigPath;

        public static string PortablePath
        {
            get { return Path.Combine(AppPaths.ExeDir, "SnipTranslate.ini"); }
        }

        public static string RoamingPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SnipTranslate");
                return Path.Combine(dir, "SnipTranslate.ini");
            }
        }

        public static AppConfig Load()
        {
            var cfg = new AppConfig();

            string path = PortablePath;
            Dictionary<string, string> values = TryRead(path);

            if (values == null)
            {
                path = RoamingPath;
                values = TryRead(path);
            }
            if (values == null)
            {
                path = PortablePath;
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            cfg.ConfigPath = path;
            cfg.Apply(values);

            if (!File.Exists(path)) cfg.Save();
            return cfg;
        }

        private static Dictionary<string, string> TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = StripInlineComment(line.Substring(eq + 1)).Trim();
                    dict[k] = v;
                }
                return dict;
            }
            catch (Exception ex)
            {
                Log.Warn("读取配置失败 " + path + " : " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 去掉值后面的行内注释。只有 "#" / ";" 前面是空白（或行首）才算注释。
        /// 这个坑吃过一次：不剥离的话 "Ctrl+D   # 划词翻译" 整个串都会被当成热键，热键全部注册失败。
        /// </summary>
        private static string StripInlineComment(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '#' && c != ';') continue;
                if (i == 0 || char.IsWhiteSpace(value[i - 1])) return value.Substring(0, i);
            }
            return value;
        }

        private void Apply(Dictionary<string, string> v)
        {
            HotkeyGrab = Get(v, "HotkeyGrab", HotkeyGrab);
            HotkeySnip = Get(v, "HotkeySnip", HotkeySnip);
            HotkeyOcr = Get(v, "HotkeyOcr", HotkeyOcr);
            HotkeySpeak = Get(v, "HotkeySpeak", HotkeySpeak);
            HotkeyInput = Get(v, "HotkeyInput", HotkeyInput);

            UseKeyboardHook = GetBool(v, "UseKeyboardHook", UseKeyboardHook);
            CloseOnBlur = GetBool(v, "CloseOnBlur", CloseOnBlur);
            AutoSpeak = GetBool(v, "AutoSpeak", AutoSpeak);
            KeepClipboard = GetBool(v, "KeepClipboard", KeepClipboard);
            StartupTip = GetBool(v, "StartupTip", StartupTip);

            EngineOrder = Get(v, "EngineOrder", EngineOrder);
            ShowPhonetic = GetBool(v, "ShowPhonetic", ShowPhonetic);

            OcrLanguage = Get(v, "OcrLanguage", OcrLanguage);
            OcrScale = GetInt(v, "OcrScale", OcrScale, 1, 4);
            SpeechRate = GetInt(v, "SpeechRate", SpeechRate, -10, 10);
            TtsEngine = Get(v, "TtsEngine", TtsEngine);
            TtsVoice = Get(v, "TtsVoice", TtsVoice);
            PrefetchSpeech = GetBool(v, "PrefetchSpeech", PrefetchSpeech);
            ClipboardWaitMs = GetInt(v, "ClipboardWaitMs", ClipboardWaitMs, 30, 3000);

            Theme = Get(v, "Theme", Theme);
            WindowWidth = GetInt(v, "WindowWidth", WindowWidth, 240, 1600);
            MaxHeightPercent = GetInt(v, "MaxHeightPercent", MaxHeightPercent, 20, 95);
            TimeoutMs = GetInt(v, "TimeoutMs", TimeoutMs, 1000, 60000);
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("# SnipTranslate 配置文件");
                sb.AppendLine("# 改完保存，右键托盘图标 →「重新加载配置」即可生效（热键会重新注册）");
                sb.AppendLine();
                sb.AppendLine("# ---- 热键 ----");
                sb.AppendLine("# 修饰键写 Ctrl / Alt / Shift / Win，主键写字母、数字或 F1~F24，用 + 连接");
                sb.AppendLine("HotkeyGrab  = " + HotkeyGrab + "        # 划词翻译：选中文字后按，直接出译文");
                sb.AppendLine("HotkeySnip  = " + HotkeySnip + "        # 截图翻译：框选屏幕区域，OCR 后翻译");
                sb.AppendLine();
                sb.AppendLine("# 下面三个默认留空 = 不占用任何快捷键（功能仍可从托盘右键菜单调用）。");
                sb.AppendLine("# 想用就填上组合键，例如 Ctrl+O / Ctrl+Alt+R / Ctrl+Alt+A，保存后重新加载配置。");
                sb.AppendLine("HotkeyOcr   = " + HotkeyOcr + "        # 截图 OCR：只识别文字，不翻译");
                sb.AppendLine("HotkeySpeak = " + HotkeySpeak + "    # 朗读上一次的原文（美式发音）");
                sb.AppendLine("HotkeyInput = " + HotkeyInput + "    # 输入翻译：手动敲一段文字");
                sb.AppendLine();
                sb.AppendLine("# ---- 行为 ----");
                sb.AppendLine("UseKeyboardHook = " + Bool(UseKeyboardHook) + "     # 底层键盘钩子兜底；关掉可排查钩子引起的异常");
                sb.AppendLine("CloseOnBlur     = " + Bool(CloseOnBlur) + "     # 结果窗口失去焦点就自动关");
                sb.AppendLine("AutoSpeak       = " + Bool(AutoSpeak) + "     # 出结果后自动朗读原文");
                sb.AppendLine("KeepClipboard   = " + Bool(KeepClipboard) + "     # 取词后把你原来的剪贴板内容放回去");
                sb.AppendLine("StartupTip      = " + Bool(StartupTip) + "     # 启动时弹一下托盘气泡");
                sb.AppendLine();
                sb.AppendLine("# ---- 内容 ----");
                sb.AppendLine("# 翻译源顺序，按先后尝试，第一个成功就用它；支持 google / youdao / mymemory");
                sb.AppendLine("EngineOrder  = " + EngineOrder);
                sb.AppendLine("ShowPhonetic = " + Bool(ShowPhonetic) + "     # 查单个单词时在译文上方显示美式音标");
                sb.AppendLine();
                sb.AppendLine("# ---- OCR / 发音 ----");
                sb.AppendLine("# OCR 语言：auto = 优先英文引擎，没有就退回系统语言；也可写死 en-US / zh-Hans-CN");
                sb.AppendLine("OcrLanguage = " + OcrLanguage);
                sb.AppendLine("OcrScale    = " + OcrScale + "        # 截图送识别前放大倍数，小字识别不准时可调到 3");
                sb.AppendLine("SpeechRate  = " + SpeechRate + "        # 语速，-10 最慢 ~ 10 最快");
                sb.AppendLine();
                sb.AppendLine("# 发音引擎：auto = 优先用 Edge 神经网络语音（最自然，需联网），");
                sb.AppendLine("#           合成失败会自动退回系统语音（Zira，离线但比较机械）");
                sb.AppendLine("#           也可以写死 edge 或 sapi");
                sb.AppendLine("TtsEngine = " + TtsEngine);
                sb.AppendLine("# Edge 语音名（年轻女声推荐）：");
                sb.AppendLine("#   en-US-AvaNeural    最有亲和力（Expressive/Caring/Pleasant/Friendly）");
                sb.AppendLine("#   en-US-EmmaNeural   活泼（Cheerful/Conversational）");
                sb.AppendLine("#   en-US-JennyNeural  温柔（Friendly/Considerate/Comfort）");
                sb.AppendLine("#   en-US-AriaNeural   自信（Positive/Confident）");
                sb.AppendLine("TtsVoice = " + TtsVoice);
                sb.AppendLine("# 结果一出来就在后台把发音合成好，点朗读时不用等（合成要 1~2 秒）。");
                sb.AppendLine("# 关掉的话点朗读会先等 1~2 秒才出声");
                sb.AppendLine("PrefetchSpeech = " + (PrefetchSpeech ? "true" : "false"));
                sb.AppendLine("# 取词时等剪贴板变化的预算（毫秒）。决定「没选中文字」时按 Ctrl+D");
                sb.AppendLine("# 要等多久才弹输入窗，越小越跟手。有选中内容时实测 25ms 就好，");
                sb.AppendLine("# 万一某个程序取不到词，把它调大（比如 500）");
                sb.AppendLine("ClipboardWaitMs = " + ClipboardWaitMs);
                sb.AppendLine();
                sb.AppendLine("# ---- 窗口 ----");
                sb.AppendLine("# 结果窗口高度自动贴合内容、不留白；只有内容过长时才限制到屏幕的这个百分比");
                sb.AppendLine("Theme            = " + Theme + "     # dark（仿 VS Code 暗色）/ light");
                sb.AppendLine("WindowWidth      = " + WindowWidth);
                sb.AppendLine("MaxHeightPercent = " + MaxHeightPercent);
                sb.AppendLine("TimeoutMs        = " + TimeoutMs + "     # 单次网络请求超时（毫秒）");

                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(true));
                Log.Info("配置已保存: " + ConfigPath);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败: " + ex.Message);
            }
        }

        private static string Bool(bool b) { return b ? "true" : "false"; }

        private static string Get(Dictionary<string, string> v, string key, string fallback)
        {
            string s;
            if (v.TryGetValue(key, out s) && !string.IsNullOrEmpty(s)) return s.Trim();
            return fallback;
        }

        private static bool GetBool(Dictionary<string, string> v, string key, bool fallback)
        {
            string s;
            if (!v.TryGetValue(key, out s)) return fallback;
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            return fallback;
        }

        private static int GetInt(Dictionary<string, string> v, string key, int fallback, int min, int max)
        {
            string s;
            if (!v.TryGetValue(key, out s)) return fallback;
            int n;
            if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return fallback;
            if (n < min) n = min;
            if (n > max) n = max;
            return n;
        }
    }
}
