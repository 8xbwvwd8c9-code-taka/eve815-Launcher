using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EveLauncher
{
    /// <summary>
    /// 极简 INI 解析：只支持 [section] / key=value / ; 与 # 注释。
    /// 键名不区分大小写；值保留原样（仅 Trim），支持行内 ; 注释。
    /// </summary>
    internal sealed class IniFile
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sections =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static IniFile Parse(string text)
        {
            var ini = new IniFile();
            var cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ini._sections[string.Empty] = cur;

            using (var sr = new StringReader(text ?? string.Empty))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    string s = line.Trim();
                    if (s.Length == 0 || s[0] == ';' || s[0] == '#') continue;

                    if (s[0] == '[')
                    {
                        int close = s.IndexOf(']');
                        if (close > 0)
                        {
                            string name = s.Substring(1, close - 1).Trim();
                            if (!ini._sections.TryGetValue(name, out cur))
                            {
                                cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                ini._sections[name] = cur;
                            }
                        }
                        continue;
                    }

                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = s.Substring(0, eq).Trim();
                    string val = s.Substring(eq + 1).Trim();

                    int cmt = val.IndexOf(';');
                    if (cmt >= 0) val = val.Substring(0, cmt).Trim();

                    cur[key] = val;
                }
            }
            return ini;
        }

        public static IniFile Load(string path, Encoding enc)
        {
            return Parse(File.ReadAllText(path, enc));
        }

        public bool HasSection(string section)
        {
            return _sections.ContainsKey(section ?? string.Empty);
        }

        public string Get(string section, string key, string fallback)
        {
            Dictionary<string, string> d;
            if (!_sections.TryGetValue(section ?? string.Empty, out d)) return fallback;
            string v;
            return d.TryGetValue(key, out v) ? v : fallback;
        }

        public int GetInt(string section, string key, int fallback)
        {
            int v;
            return int.TryParse(Get(section, key, null), out v) ? v : fallback;
        }

        public uint GetUInt(string section, string key, uint fallback)
        {
            string s = Get(section, key, null);
            if (string.IsNullOrEmpty(s)) return fallback;

            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                uint hex;
                return uint.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber,
                                     System.Globalization.CultureInfo.InvariantCulture, out hex) ? hex : fallback;
            }
            uint dec;
            return uint.TryParse(s, out dec) ? dec : fallback;
        }

        /// <summary>读一个地址类数值，支持 0x 前缀十六进制（补丁 VA 用）。</summary>
        public long GetLongHex(string section, string key, long fallback)
        {
            string s = Get(section, key, null);
            if (string.IsNullOrEmpty(s)) return fallback;

            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                long hex;
                return long.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber,
                                     System.Globalization.CultureInfo.InvariantCulture, out hex) ? hex : fallback;
            }
            long dec;
            return long.TryParse(s, out dec) ? dec : fallback;
        }

        public bool GetBool(string section, string key, bool fallback)
        {
            string s = Get(section, key, null);
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim();
            if (s == "1") return true;
            if (s == "0") return false;
            bool b;
            return bool.TryParse(s, out b) ? b : fallback;
        }

        /// <summary>写入/覆盖一个键值（不存在的 section 会自动创建）。</summary>
        public void Set(string section, string key, string value)
        {
            Dictionary<string, string> d;
            if (!_sections.TryGetValue(section ?? string.Empty, out d))
            {
                d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _sections[section ?? string.Empty] = d;
            }
            d[key] = value ?? string.Empty;
        }

        /// <summary>序列化为 INI 文本（空 section 不写 [header]）。</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var kv in _sections)
            {
                if (kv.Key.Length > 0)
                {
                    sb.Append('[').Append(kv.Key).Append(']').Append('\n');
                }
                foreach (var e in kv.Value)
                    sb.Append(e.Key).Append('=').Append(e.Value).Append('\n');
                if (kv.Key.Length > 0) sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
