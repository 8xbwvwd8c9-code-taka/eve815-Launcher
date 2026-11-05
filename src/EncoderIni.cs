using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EveLauncher
{
    /// <summary>Encoder.ini 里的一个服务器槽位。</summary>
    internal sealed class ServerEntry
    {
        public int Slot;
        public string Name = "";
        public string Ip = "";
        public int Port = 2000;
        public int UseBd = 1;
        public string BdFile = "";
        public int Helper = 1;
        public int Encrypt = 1;
        public int RandKey;

        public override string ToString()
        {
            return string.Format("槽{0} {1} {2}:{3} usebd={4} bdfile={5} helper={6}",
                                 Slot, Name, Ip, Port, UseBd, BdFile, Helper);
        }
    }

    /// <summary>
    /// Encoder.ini 解析器。文件是 BIG5（code page 950），
    /// 只取 [ServerList] 的 8 个槽位里 enable=1 的项，按槽位顺序输出。
    /// </summary>
    internal static class EncoderIni
    {
        public const int SlotCount = 8;

        private static Encoding _big5;
        private static Encoding Big5
        {
            get
            {
                if (_big5 == null)
                {
                    // 注册 CodePagesEncodingProvider 之外的兜底：.NET Framework 原生支持 950
                    try { _big5 = Encoding.GetEncoding(950); }
                    catch (Exception) { _big5 = Encoding.Default; }
                }
                return _big5;
            }
        }

        public static List<ServerEntry> ReadEnabled(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到 Encoder.ini：" + path);

            var ini = IniFile.Parse(File.ReadAllText(path, Big5));
            var list = new List<ServerEntry>();

            for (int i = 1; i <= SlotCount; i++)
            {
                if (ini.GetInt("ServerList", "server_enable" + i, 0) != 1) continue;

                var e = new ServerEntry
                {
                    Slot = i,
                    Name = ini.Get("ServerList", "server_name" + i, "").Trim(),
                    Ip = ini.Get("ServerList", "server_ip" + i, "").Trim(),
                    Port = ini.GetInt("ServerList", "server_port" + i, 2000),
                    UseBd = ini.GetInt("ServerList", "server_usebd" + i, 1),
                    BdFile = ini.Get("ServerList", "server_bdfile" + i, "").Trim(),
                    Helper = ini.GetInt("ServerList", "server_helper" + i, 1),
                    Encrypt = ini.GetInt("ServerList", "server_encrypt" + i, 1),
                    RandKey = ini.GetInt("ServerList", "server_randkey" + i, 0)
                };

                if (e.Name.Length == 0)
                    throw new InvalidDataException("Encoder.ini 槽位 " + i + " 已启用但 server_name 为空");
                if (e.Ip.Length == 0)
                    throw new InvalidDataException("Encoder.ini 槽位 " + i + " 已启用但 server_ip 为空");
                if (e.Port < 1 || e.Port > 65535)
                    throw new InvalidDataException("Encoder.ini 槽位 " + i + " 端口不合法：" + e.Port);

                list.Add(e);
            }

            if (list.Count == 0)
                throw new InvalidDataException("Encoder.ini 里没有任何 server_enableN=1 的服务器：" + path);

            return list;
        }
    }
}
