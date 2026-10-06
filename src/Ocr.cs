using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SnipTranslate
{
    /// <summary>
    /// 直接调用 Windows 自带的 OCR 引擎（Windows.Media.Ocr），进程内、离线、无需额外安装。
    /// 实测一次识别 20~60ms。
    /// </summary>
    internal static class Ocr
    {
        private static readonly object Gate = new object();
        private static OcrEngine _engine;
        private static string _preferred = "auto";

        /// <summary>配置里指定的识别语言；auto = 优先英文引擎，没有就退回系统语言。</summary>
        public static void Configure(string languageTag)
        {
            lock (Gate)
            {
                string v = string.IsNullOrEmpty(languageTag) ? "auto" : languageTag.Trim();
                if (v == _preferred && _engine != null) return;
                _preferred = v;
                _engine = null;
            }
        }

        public static List<string> AvailableLanguages()
        {
            var list = new List<string>();
            try
            {
                foreach (Language l in OcrEngine.AvailableRecognizerLanguages) list.Add(l.LanguageTag);
            }
            catch (Exception ex) { Log.Warn("枚举 OCR 语言失败: " + ex.Message); }
            return list;
        }

        public static string EngineLanguage
        {
            get
            {
                lock (Gate) { EnsureEngine(); return _engine == null ? "(不可用)" : _engine.RecognizerLanguage.LanguageTag; }
            }
        }

        public static bool IsAvailable
        {
            get { lock (Gate) { EnsureEngine(); return _engine != null; } }
        }

        private static void EnsureEngine()
        {
            if (_engine != null) return;
            try
            {
                if (!string.Equals(_preferred, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    _engine = OcrEngine.TryCreateFromLanguage(new Language(_preferred));
                    if (_engine != null)
                    {
                        Log.Info("OCR 引擎就绪（配置指定），语言 = " + _engine.RecognizerLanguage.LanguageTag);
                        return;
                    }
                    Log.Warn("指定的 OCR 语言不可用: " + _preferred + "，改回自动选择");
                }

                // auto：本工具主要翻英文，优先英文引擎（装了英文 OCR 语言功能才有）
                _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
                if (_engine != null) { Log.Info("OCR 引擎就绪（英文），语言 = en-US"); return; }

                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine == null) Log.Warn("OcrEngine.TryCreateFromUserProfileLanguages() 返回 null");
                else Log.Info("OCR 引擎就绪（系统语言），语言 = " + _engine.RecognizerLanguage.LanguageTag);
            }
            catch (Exception ex)
            {
                Log.Error("创建 OCR 引擎失败", ex);
                _engine = null;
            }
        }

        /// <summary>识别一张位图，返回整理过的文本。失败抛异常。</summary>
        public static string Recognize(Bitmap bitmap)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");

            lock (Gate)
            {
                EnsureEngine();
                if (_engine == null)
                    throw new InvalidOperationException(
                        "系统 OCR 引擎不可用。请在「设置 → 时间和语言 → 语言」里确认已安装中文或英文语言包。");

                string path = Path.Combine(AppPaths.TempDir, "ocr_" + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    bitmap.Save(path, ImageFormat.Png);
                    // 放到线程池上跑：async 续体不带 UI 同步上下文，避免阻塞 UI 线程时死锁
                    // 注意：RecognizeFileAsync 内部已经做过单行清理和按行重组，这里不要再加工
                    return Task.Run(() => RecognizeFileAsync(path)).GetAwaiter().GetResult();
                }
                finally
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
            }
        }

        private static async Task<string> RecognizeFileAsync(string path)
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            // 注意：BitmapDecoder 在这个投影里不是 IDisposable，不能 using
            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
            {
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                using (SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync())
                {
                    OcrResult result = await _engine.RecognizeAsync(bitmap);
                    return BuildText(result);
                }
            }
        }

        /// <summary>
        /// 按行重组识别结果，尽量保留原来的排版。
        ///
        /// 不能简单地把所有行用空格拼起来（那样整段会变成一句话），
        /// 也不能每行都换行（OCR 经常把同一句话按屏幕宽度折成好几行）。
        /// 判据：上一行以句末标点结尾 → 认为一段结束，换行；
        ///       下一行以小写字母或收尾标点开头 → 认为是同一句的续行，用空格接上。
        /// </summary>
        private static string BuildText(OcrResult result)
        {
            var lines = new List<string>();
            if (result != null && result.Lines != null)
            {
                foreach (OcrLine line in result.Lines)
                {
                    string t = CleanLine(line.Text);
                    if (t.Length > 0) lines.Add(t);
                }
            }
            if (lines.Count == 0) return "";

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append(IsContinuation(lines[i - 1], lines[i]) ? ' ' : '\n');
                sb.Append(lines[i]);
            }
            return sb.ToString();
        }

        private static bool IsContinuation(string prev, string next)
        {
            if (string.IsNullOrEmpty(prev) || string.IsNullOrEmpty(next)) return false;

            // 上一行已经是句末 → 新的一段
            char last = prev[prev.Length - 1];
            if (".!?;:。！？；：…".IndexOf(last) >= 0) return false;

            // 下一行是小写开头，或紧跟着收尾标点 → 同一句的续行
            char first = next[0];
            if (first >= 'a' && first <= 'z') return true;
            if (",)]}，）】、".IndexOf(first) >= 0) return true;

            return false;
        }

        /// <summary>单行内的清理：合并多余空白、给粘连的单词补空格、去掉标点前的空格。</summary>
        private static string CleanLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string t = text.Replace("\r", " ").Replace("\n", " ");
            t = Regex.Replace(t, @"[ \t\u00A0\u3000]+", " ");
            t = Regex.Replace(t, @"(?<=[a-z])(?=[A-Z][a-z])", " ");
            t = Regex.Replace(t, @" +([,\.\?;:!，。！？；：、])", "$1");
            return t.Trim();
        }
    }
}
