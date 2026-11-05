using System;
using System.IO;
using System.Text;

namespace EveLauncher
{
    /// <summary>极简日志：写文件 + 可选控制台镜像。绝不写密码等敏感值。</summary>
    internal static class Logger
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _echo;

        public static void Init(string path, bool echo)
        {
            _path = path;
            _echo = echo;
            Info("==== EveLauncher 启动 ====");
        }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}",
                                        DateTime.Now, level, msg);
            if (_echo)
            {
                try { Console.WriteLine(line); } catch (Exception) { }
            }
            if (string.IsNullOrEmpty(_path)) return;

            lock (Gate)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false)); }
                catch (Exception) { /* 日志失败不影响主流程 */ }
            }
        }
    }
}
