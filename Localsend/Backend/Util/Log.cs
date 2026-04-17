using System;
using System.Diagnostics;
using System.Globalization;

namespace Localsend.Backend.Util
{
    /// <summary>轻量日志。调试期走 Debug.WriteLine，后续可替换为文件输出。</summary>
    internal static class Log
    {
        public static void Info(string msg) { Debug.WriteLine("[INFO ] " + msg); }
        public static void Warn(string msg) { Debug.WriteLine("[WARN ] " + msg); }
        public static void Error(string msg) { Debug.WriteLine("[ERROR] " + msg); }
        public static void Error(string msg, Exception ex) { Debug.WriteLine("[ERROR] " + msg + ": " + ex); }
    }

    internal static class IdGen
    {
        private static readonly Random _rng = new Random();
        private static readonly object _lock = new object();

        /// <summary>生成随机指纹/令牌。16 字节十六进制。</summary>
        public static string NewRandom()
        {
            byte[] buf = new byte[16];
            lock (_lock) { _rng.NextBytes(buf); }
            char[] hex = new char[32];
            const string table = "0123456789abcdef";
            for (int i = 0; i < 16; i++)
            {
                hex[i * 2] = table[buf[i] >> 4];
                hex[i * 2 + 1] = table[buf[i] & 0xF];
            }
            return new string(hex);
        }
    }

    /// <summary>CF 3.5 上 TryParse 重载不稳，手写安全解析。</summary>
    internal static class Parse
    {
        public static bool TryLong(string s, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            try { value = long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }

        public static bool TryBool(string s, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(s)) return false;
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
            return false;
        }
    }
}

