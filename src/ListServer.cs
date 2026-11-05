using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace EveLauncher
{
    /// <summary>
    /// 本机极简 HTTP 服务，只为把 list.txt 喂给原版 LinLauncher.exe。
    /// 刻意用 TcpListener 裸写 HTTP/1.0，不用 HttpListener —— 后者走 http.sys，
    /// 非管理员且未注册 URL ACL 时会直接 503，是笔记里踩过的坑。
    /// </summary>
    internal sealed class ListServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _body;
        private volatile bool _running;
        private Thread _thread;

        public int Port { get; private set; }

        private int _requests;
        /// <summary>已处理的请求数，供启动后诊断用。</summary>
        public int RequestCount { get { return System.Threading.Volatile.Read(ref _requests); } }

        private ListServer(TcpListener listener, int port, byte[] body)
        {
            _listener = listener;
            Port = port;
            _body = body;
        }

        /// <summary>绑定失败会抛 SocketException，绝不静默降级。</summary>
        public static ListServer Start(int port, byte[] body)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.ExclusiveAddressUse = true;
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                listener.Stop();
                throw new InvalidOperationException(
                    "无法绑定 127.0.0.1:" + port + "（可能被 hfs.exe 或另一个 EveLauncher 占用）。\n" +
                    "原始错误：" + ex.Message, ex);
            }

            var srv = new ListServer(listener, port, body);
            srv._running = true;
            srv._thread = new Thread(srv.AcceptLoop);
            srv._thread.IsBackground = true;
            srv._thread.Name = "ListServer";
            srv._thread.Start();
            return srv;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }

                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        private void HandleClient(object state)
        {
            using (var client = (TcpClient)state)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    System.Threading.Interlocked.Increment(ref _requests);

                    using (var ns = client.GetStream())
                    {
                        string requestLine = ReadRequestHeaders(ns);
                        string path = ParsePath(requestLine);
                        bool serve = path == "/" || path.Equals("/list.txt", StringComparison.OrdinalIgnoreCase);

                        Logger.Info("HTTP " + (requestLine.Length > 0 ? requestLine : "(空请求)")
                                    + " → " + (serve ? "200 " + _body.Length + "B" : "404"));

                        byte[] head = serve ? HeadOk(_body.Length) : HeadNotFound();
                        ns.Write(head, 0, head.Length);
                        if (serve) ns.Write(_body, 0, _body.Length);
                        ns.Flush();

                        // 必须优雅收尾：若内核接收缓冲还有未读数据就关 socket，
                        // Windows 会发 RST 把已写出的响应一起丢掉（WinINet 会当成下载失败）。
                        // 这里先 shutdown(Send) 再让 using 关闭，客户端能收到完整响应 + FIN。
                        try { client.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                        try
                        {
                            var drain = new byte[512];
                            int guard = 0;
                            while (ns.DataAvailable && guard++ < 16) ns.Read(drain, 0, drain.Length);
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("ListServer 处理请求失败：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 把整个请求头读干净（到空行为止），返回请求行。
        /// 只读请求行会留下未读数据，关 socket 时触发 RST，响应会被客户端丢弃。
        /// </summary>
        private static string ReadRequestHeaders(NetworkStream ns)
        {
            var all = new StringBuilder();
            var buf = new byte[1024];
            int total = 0;

            while (total < 16384)
            {
                int n;
                try { n = ns.Read(buf, 0, buf.Length); }
                catch (System.IO.IOException) { break; }
                if (n <= 0) break;
                total += n;
                all.Append(Encoding.ASCII.GetString(buf, 0, n));

                string s = all.ToString();
                int end = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) end = s.IndexOf("\n\n", StringComparison.Ordinal);
                if (end >= 0) break;
            }

            string text = all.ToString();
            int eol = text.IndexOf('\n');
            return (eol < 0 ? text : text.Substring(0, eol)).TrimEnd('\r');
        }

        private static string ParsePath(string requestLine)
        {
            if (string.IsNullOrEmpty(requestLine)) return "/";
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return "/";
            string p = parts[1];
            int q = p.IndexOf('?');
            if (q >= 0) p = p.Substring(0, q);
            return p.Length == 0 ? "/" : p;
        }

        private static byte[] HeadOk(int len)
        {
            return Encoding.ASCII.GetBytes(
                "HTTP/1.0 200 OK\r\n" +
                "Content-Type: text/plain; charset=us-ascii\r\n" +
                "Content-Length: " + len + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");
        }

        private static byte[] HeadNotFound()
        {
            return Encoding.ASCII.GetBytes(
                "HTTP/1.0 404 Not Found\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n");
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch (Exception) { }
            if (_thread != null)
            {
                try { _thread.Join(2000); } catch (Exception) { }
                _thread = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
