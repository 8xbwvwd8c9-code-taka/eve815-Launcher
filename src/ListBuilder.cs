using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EveLauncher
{
    /// <summary>
    /// list.txt 的 213 字节记录构造 + 加密 + 文件拼装。
    /// 生成與驗證 815 客戶端使用的加密清單記錄。
    /// </summary>
    internal static class ListBuilder
    {
        // ---- 已验证常量 ----------------------------------------------------
        public const int RecordLength = 213;              // 0xD5
        private const int AesBlockBytes = 224;            // ceil(213/16)*16

        /// <summary>清單 AES-128 金鑰由 EVE815_LIST_AES_KEY 環境變數提供。</summary>
        private static byte[] Key
        {
            get
            {
                string value = Environment.GetEnvironmentVariable("EVE815_LIST_AES_KEY");
                if (string.IsNullOrEmpty(value) || value.Length != 16)
                    throw new InvalidOperationException("EVE815_LIST_AES_KEY 必須是 16 個可列印 ASCII 字元。");
                for (int i = 0; i < value.Length; i++)
                    if (value[i] < 0x21 || value[i] > 0x7e)
                        throw new InvalidOperationException("EVE815_LIST_AES_KEY 必須是 16 個可列印 ASCII 字元。");
                return Encoding.ASCII.GetBytes(value);
            }
        }

        /// <summary>记录 0xD0..0xD4 的常量尾块（不是 0，写 0 会导致密文尾 5 字节不符）。</summary>
        private static readonly byte[] Trailer = { 0xC5, 0x59, 0x0F, 0xFD, 0xB2 };

        /// <summary>填充字节用 0xFE 而非 0x00（Encoder 风格）。</summary>
        private const byte Fill = 0xFE;

        /// <summary>
        /// v1 固定值：记录 0x77。
        /// 实测 Encoder.ini 的 server_encryptN=0 时，两端已知可用记录的 0x77 仍是 1，
        /// 说明该 ini 键不控制这一字节，故按常量 1 处理。
        /// </summary>
        private const byte RecordEncryptByte = 0x01;

        private static readonly byte[] Ks = BuildKeystream();

        private static byte[] BuildKeystream()
        {
            var ks = new byte[256];
            for (int i = 0; i < 256; i++)
                ks[i] = (byte)(FixedTable.Bytes[i] ^ Key[i % 16]);
            return ks;
        }

        // ---- 记录构造 ------------------------------------------------------

        /// <summary>按已确认布局构造 213 字节明文记录。</summary>
        public static byte[] BuildRecord(ServerEntry s, uint global1, uint global2)
        {
            var r = new byte[RecordLength];
            for (int i = 0; i < RecordLength; i++) r[i] = Fill;

            // 0x00 名称 UTF-16LE + 00 00
            byte[] nb = Encoding.Unicode.GetBytes(s.Name ?? "");
            int nlen = Math.Min(nb.Length, 0x3E);           // 0x40 区段留 2 字节终止符
            Array.Copy(nb, 0, r, 0x00, nlen);
            r[0x00 + nlen] = 0x00;
            r[0x01 + nlen] = 0x00;

            // 0x40 IP ANSI + 00
            byte[] hb = Encoding.ASCII.GetBytes(s.Ip ?? "");
            int hlen = Math.Min(hb.Length, 0x1F);           // 0x20 区段留 1 字节终止符
            Array.Copy(hb, 0, r, 0x40, hlen);
            r[0x40 + hlen] = 0x00;

            // 0x60 端口 u32LE
            WriteU32(r, 0x60, (uint)s.Port);

            // 0x64 吃档开关
            r[0x64] = (byte)(s.UseBd != 0 ? 1 : 0);

            // 0x65..0x75 备用 IP 区 = 0（不是 0xFE）
            for (int i = 0x65; i <= 0x75; i++) r[i] = 0x00;

            // 0x76 helper / 0x77 encrypt
            r[0x76] = (byte)(s.Helper != 0 ? 1 : 0);
            r[0x77] = RecordEncryptByte;

            // 0x78 吃档 pak UTF-16LE + 00 00，带 "./" 前缀
            string pak = s.BdFile ?? "";
            if (pak.Length > 0 && !pak.StartsWith("./") && !pak.StartsWith(".\\"))
                pak = "./" + pak;
            byte[] db = Encoding.Unicode.GetBytes(pak);
            int dlen = Math.Min(db.Length, 0x3E);           // 0x40 区段留 2 字节终止符
            Array.Copy(db, 0, r, 0x78, dlen);
            r[0x78 + dlen] = 0x00;
            r[0x79 + dlen] = 0x00;

            // 0xB8 randkey u32LE / 0xBC = 0
            WriteU32(r, 0xB8, (uint)s.RandKey);
            r[0xBC] = 0x00;

            // 0xBD / 0xC1 两个全局值（LauncherDll 拿它做登入翻译金钥材料，顺序不可对调）
            WriteU32(r, 0xBD, global1);
            WriteU32(r, 0xC1, global2);

            // 0xC5..0xCF = 0
            for (int i = 0xC5; i <= 0xCF; i++) r[i] = 0x00;

            // 0xD0 常量尾块
            Array.Copy(Trailer, 0, r, 0xD0, Trailer.Length);

            return r;
        }

        private static void WriteU32(byte[] buf, int off, uint v)
        {
            buf[off + 0] = (byte)(v & 0xFF);
            buf[off + 1] = (byte)((v >> 8) & 0xFF);
            buf[off + 2] = (byte)((v >> 16) & 0xFF);
            buf[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static uint ReadU32(byte[] buf, int off)
        {
            return (uint)(buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24));
        }

        // ---- 加密 ----------------------------------------------------------

        /// <summary>
        /// 213 字节记录 → Base64（284 字符）。
        /// 要点：① 只 XOR 前 213 字节，后 11 字节保持 0；② 密文截断到 213 字节再 base64。
        /// </summary>
        public static string Encrypt(byte[] record)
        {
            if (record == null || record.Length != RecordLength)
                throw new ArgumentException("record 必须是 " + RecordLength + " 字节");

            var buf = new byte[AesBlockBytes];
            Array.Copy(record, 0, buf, 0, RecordLength);
            for (int i = 0; i < RecordLength; i++) buf[i] ^= Ks[i];

            byte[] ct;
            using (var aes = new RijndaelManaged())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.BlockSize = 128;
                aes.Key = Key;
                using (var enc = aes.CreateEncryptor())
                    ct = enc.TransformFinalBlock(buf, 0, AesBlockBytes);
            }

            return Convert.ToBase64String(ct, 0, RecordLength);
        }

        /// <summary>Base64 → 213 字节记录。末块不完整，208..212 强制还原为常量尾块。</summary>
        public static byte[] Decrypt(string base64)
        {
            byte[] ct = Convert.FromBase64String(base64);
            var buf = new byte[AesBlockBytes];
            Array.Copy(ct, 0, buf, 0, Math.Min(ct.Length, AesBlockBytes));

            byte[] pt;
            using (var aes = new RijndaelManaged())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.BlockSize = 128;
                aes.Key = Key;
                using (var dec = aes.CreateDecryptor())
                    pt = dec.TransformFinalBlock(buf, 0, AesBlockBytes);
            }

            var r = new byte[RecordLength];
            for (int i = 0; i < RecordLength; i++) r[i] = (byte)(pt[i] ^ Ks[i]);
            Array.Copy(Trailer, 0, r, 0xD0, Trailer.Length);
            return r;
        }

        // ---- 文件拼装 ------------------------------------------------------

        private static readonly byte[] DefaultTemplate = Encoding.ASCII.GetBytes(
            "[update]\r\nver=8.15c\r\nurl=\r\n[list]\r\n");

        /// <summary>
        /// 用模板的 [update] 段（逐字节照抄到 [list] 行末）＋ 新的 ServerDataN 行拼出 list.txt。
        /// 模板为 null 时用内建默认头。
        /// </summary>
        public static byte[] BuildFile(byte[] template, IList<string> base64Records)
        {
            if (base64Records == null || base64Records.Count == 0)
                throw new ArgumentException("至少要有一条记录");

            byte[] header = ExtractHeader(template);
            string nl = DetectNewLine(header);
            CheckTemplateVersion(header);

            var sb = new StringBuilder();
            sb.Append(Encoding.ASCII.GetString(header));
            for (int i = 0; i < base64Records.Count; i++)
                sb.Append("ServerData").Append(i).Append('=').Append(base64Records[i]).Append(nl);

            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static byte[] ExtractHeader(byte[] template)
        {
            if (template == null || template.Length == 0) return DefaultTemplate;

            byte[] needle = Encoding.ASCII.GetBytes("[list]");
            int idx = IndexOf(template, needle, 0, true);
            if (idx < 0) return DefaultTemplate;

            int eol = idx;
            while (eol < template.Length && template[eol] != (byte)'\n') eol++;
            if (eol < template.Length) eol++;              // 吃掉换行符

            var header = new byte[eol];
            Array.Copy(template, 0, header, 0, eol);
            return header;
        }

        /// <summary>
        /// launcher 在 0x4b15bb 用 _wtoi64(自身 ver) 与清单 ver 比较，清单更大就触发自我更新。
        /// 所以清单 ver 必须 <= 1001。8.15c 被解析成 8（只取前导数字），安全。
        /// </summary>
        private static void CheckTemplateVersion(byte[] header)
        {
            string text = Encoding.ASCII.GetString(header);
            using (var sr = new StringReader(text))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    string s = line.Trim();
                    if (!s.StartsWith("ver", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = s.IndexOf('=');
                    if (eq < 0) continue;

                    string v = s.Substring(eq + 1).Trim();
                    int i = 0;
                    while (i < v.Length && v[i] >= '0' && v[i] <= '9') i++;
                    long n;
                    if (i > 0 && long.TryParse(v.Substring(0, i), out n) && n > 1001)
                        throw new InvalidDataException(
                            "模板 [update] 段的 ver=" + v + " 会让 launcher 触发自我更新（阈值 1001）。" +
                            "请把 list.txt 模板的 ver 改回 8.15c 或 1001 以下。");
                    return;
                }
            }
        }

        private static string DetectNewLine(byte[] header)        {
            for (int i = 0; i < header.Length; i++)
                if (header[i] == (byte)'\n')
                    return (i > 0 && header[i - 1] == (byte)'\r') ? "\r\n" : "\n";
            return "\r\n";
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start, bool ignoreCase)
        {
            for (int i = start; i <= haystack.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    byte a = haystack[i + j], b = needle[j];
                    if (ignoreCase)
                    {
                        if (a >= 'A' && a <= 'Z') a = (byte)(a + 32);
                        if (b >= 'A' && b <= 'Z') b = (byte)(b + 32);
                    }
                    if (a != b) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        // ---- 便捷入口 ------------------------------------------------------

        public static byte[] BuildListBytes(Config cfg, out List<string> debugRecords)
        {
            return BuildListBytes(cfg, null, out debugRecords);
        }

        /// <summary>templateOverride 非空时优先使用（测试 T2 用）。</summary>
        public static byte[] BuildListBytes(Config cfg, byte[] templateOverride, out List<string> debugRecords)
        {
            var servers = EncoderIni.ReadEnabled(cfg.EncoderIni);

            byte[] template = templateOverride;
            if (template == null)
            {
                string localList = Path.Combine(cfg.ClientDir, "list.txt");
                string masterList = Path.Combine(cfg.MasterDir, "list.txt");
                if (File.Exists(localList)) template = File.ReadAllBytes(localList);
                else if (File.Exists(masterList)) template = File.ReadAllBytes(masterList);
            }

            var recs = new List<string>();
            var dbg = new List<string>();
            foreach (var s in servers)
            {
                var rec = BuildRecord(s, cfg.Global1, cfg.Global2);
                recs.Add(Encrypt(rec));
                dbg.Add(s.ToString());
            }
            debugRecords = dbg;
            return BuildFile(template, recs);
        }

        /// <summary>把记录解回可读字段，供 --diag / --selftest 打印。</summary>
        public static string Describe(byte[] rec)
        {
            string name = Encoding.Unicode.GetString(rec, 0x00, 0x40).Split('\0')[0];
            string host = Encoding.ASCII.GetString(rec, 0x40, 0x20).Split('\0')[0];
            string pak = Encoding.Unicode.GetString(rec, 0x78, 0x40).Split('\0')[0];
            return string.Format(
                "name='{0}' host='{1}' port={2} usebd={3} helper={4} encrypt={5} pak='{6}' randkey={7} g1=0x{8:X8} g2=0x{9:X8}",
                name, host, ReadU32(rec, 0x60), rec[0x64], rec[0x76], rec[0x77], pak,
                ReadU32(rec, 0xB8), ReadU32(rec, 0xBD), ReadU32(rec, 0xC1));
        }
    }
}
