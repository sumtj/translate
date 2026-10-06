using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SnipTranslate
{
    /// <summary>
    /// 自己实现的 WebSocket 客户端（TcpClient + SslStream）。
    ///
    /// 为什么不用 .NET 自带的 ClientWebSocket：**它不允许设置 User-Agent 头**
    /// （System.Net 把它列为受限标头，SetRequestHeader 直接抛异常），
    /// 而 Edge 的 TTS 服务要求带 Edge 的 UA，否则握手一律 403。
    /// 实测：带 UA 用 curl 请求返回 101 Switching Protocols，不带就 403。
    ///
    /// 所以这里手写握手和分帧，全部标头自己控制。只实现用得到的部分：
    /// 客户端发文本帧（必须加掩码）、服务端收数据帧、分片重组、关闭。
    /// </summary>
    internal class RawWebSocket : IDisposable
    {
        private TcpClient _tcp;
        private SslStream _ssl;
        private readonly List<byte> _pending = new List<byte>();   // 握手后多读进来的字节

        public bool Connected { get { return _ssl != null && _tcp != null && _tcp.Connected; } }

        /// <summary>建立连接并发起 WebSocket 握手。headers 里每个键值都会原样发出。</summary>
        public void Connect(string host, string pathAndQuery, Dictionary<string, string> headers, int timeoutMs)
        {
            _tcp = new TcpClient();
            _tcp.ReceiveTimeout = timeoutMs;
            _tcp.SendTimeout = timeoutMs;

            IAsyncResult ar = _tcp.BeginConnect(host, 443, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                throw new Exception("连接 " + host + " 超时");
            _tcp.EndConnect(ar);

            _ssl = new SslStream(_tcp.GetStream(), false, delegate { return true; });
            _ssl.ReadTimeout = timeoutMs;
            _ssl.WriteTimeout = timeoutMs;
            _ssl.AuthenticateAsClient(host, null, System.Security.Authentication.SslProtocols.Tls12, false);

            // ---- 握手请求 ----
            byte[] keyBytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(keyBytes);
            string secKey = Convert.ToBase64String(keyBytes);

            var sb = new StringBuilder();
            sb.Append("GET ").Append(pathAndQuery).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(host).Append("\r\n");
            sb.Append("Connection: Upgrade\r\n");
            sb.Append("Upgrade: websocket\r\n");
            sb.Append("Sec-WebSocket-Version: 13\r\n");
            sb.Append("Sec-WebSocket-Key: ").Append(secKey).Append("\r\n");
            sb.Append("Accept-Encoding: identity\r\n");   // 别让中间层压缩帧数据
            foreach (var kv in headers)
                sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            sb.Append("\r\n");

            byte[] req = Encoding.ASCII.GetBytes(sb.ToString());
            _ssl.Write(req, 0, req.Length);
            _ssl.Flush();

            // ---- 读响应头（逐字节读到 CRLFCRLF，避免多吞掉帧数据）----
            var head = new List<byte>();
            int state = 0;
            while (state < 4)
            {
                int b = _ssl.ReadByte();
                if (b < 0) throw new Exception("握手时连接被关闭");
                head.Add((byte)b);
                if ((state == 0 || state == 2) && b == '\r') state++;
                else if ((state == 1 || state == 3) && b == '\n') state++;
                else state = (b == '\r') ? 1 : 0;
                if (head.Count > 16384) throw new Exception("响应头过长");
            }

            string header = Encoding.ASCII.GetString(head.ToArray());
            string firstLine = header.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            if (firstLine.IndexOf(" 101", StringComparison.Ordinal) < 0)
                throw new Exception("握手被拒：" + firstLine.Trim());
        }

        // ==================== 发送 ====================

        public void SendText(string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            var buf = new MemoryStream();
            buf.WriteByte(0x81);                       // FIN + text frame

            int len = payload.Length;
            if (len < 126) buf.WriteByte((byte)(0x80 | len));           // 客户端必须加掩码
            else if (len <= 0xFFFF)
            {
                buf.WriteByte(0x80 | 126);
                buf.WriteByte((byte)(len >> 8)); buf.WriteByte((byte)(len & 0xFF));
            }
            else
            {
                buf.WriteByte(0x80 | 127);
                for (int i = 7; i >= 0; i--) buf.WriteByte((byte)((long)len >> (8 * i)));
            }

            byte[] mask = new byte[4];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(mask);
            buf.Write(mask, 0, 4);

            byte[] masked = new byte[len];
            for (int i = 0; i < len; i++) masked[i] = (byte)(payload[i] ^ mask[i & 3]);
            buf.Write(masked, 0, len);

            byte[] all = buf.ToArray();
            buf.Dispose();
            _ssl.Write(all, 0, all.Length);
            _ssl.Flush();
        }

        // ==================== 接收 ====================

        /// <summary>收一条完整消息。返回 null 表示连接关闭。isText 区分文本/二进制。</summary>
        public byte[] ReceiveMessage(out bool isText, int timeoutMs)
        {
            isText = false;
            var message = new MemoryStream();
            bool first = true;

            while (true)
            {
                int opcode;
                bool fin;
                byte[] payload = ReadFrame(out opcode, out fin, timeoutMs);

                if (opcode == 0x8) return null;                 // close
                if (opcode == 0x9) { SendPong(payload); continue; }   // ping → pong
                if (opcode == 0xA) continue;                    // pong

                if (first)
                {
                    isText = (opcode == 0x1);
                    first = false;
                }
                message.Write(payload, 0, payload.Length);

                if (fin) break;
            }

            byte[] result = message.ToArray();
            message.Dispose();
            return result;
        }

        private byte[] ReadFrame(out int opcode, out bool fin, int timeoutMs)
        {
            int b0 = ReadByteChecked(timeoutMs);
            int b1 = ReadByteChecked(timeoutMs);

            opcode = b0 & 0x0F;
            fin = (b0 & 0x80) != 0;

            long len = b1 & 0x7F;
            if (len == 126) len = ((long)ReadByteChecked(timeoutMs) << 8) | (uint)ReadByteChecked(timeoutMs);
            else if (len == 127)
            {
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | (uint)ReadByteChecked(timeoutMs);
            }

            bool masked = (b1 & 0x80) != 0;   // 服务端不该加掩码，加了也照解
            byte[] mask = null;
            if (masked)
            {
                mask = new byte[4];
                ReadExact(mask, 4, timeoutMs);
            }

            if (len > 32 * 1024 * 1024) throw new Exception("帧过大: " + len);
            byte[] payload = new byte[len];
            ReadExact(payload, (int)len, timeoutMs);

            if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
            return payload;
        }

        private void SendPong(byte[] payload)
        {
            var buf = new MemoryStream();
            buf.WriteByte(0x8A);
            buf.WriteByte((byte)(0x80 | payload.Length));
            byte[] mask = new byte[4];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(mask);
            buf.Write(mask, 0, 4);
            for (int i = 0; i < payload.Length; i++) buf.WriteByte((byte)(payload[i] ^ mask[i & 3]));
            byte[] all = buf.ToArray();
            buf.Dispose();
            try { _ssl.Write(all, 0, all.Length); _ssl.Flush(); } catch { }
        }

        private int ReadByteChecked(int timeoutMs)
        {
            if (_pending.Count > 0)
            {
                int v = _pending[0];
                _pending.RemoveAt(0);
                return v;
            }
            _ssl.ReadTimeout = timeoutMs;
            int b = _ssl.ReadByte();
            if (b < 0) throw new EndOfStreamException("连接已被服务端关闭");
            return b;
        }

        private void ReadExact(byte[] buffer, int count, int timeoutMs)
        {
            int offset = 0;

            while (offset < count && _pending.Count > 0)
                buffer[offset++] = _pending[0];

            if (offset > 0) _pending.RemoveRange(0, offset);

            _ssl.ReadTimeout = timeoutMs;
            while (offset < count)
            {
                int n = _ssl.Read(buffer, offset, count - offset);
                if (n <= 0) throw new EndOfStreamException("连接已被服务端关闭");
                offset += n;
            }
        }

        public void Dispose()
        {
            try { if (_ssl != null) { _ssl.Close(); _ssl.Dispose(); } } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
            _ssl = null;
            _tcp = null;
        }
    }
}
