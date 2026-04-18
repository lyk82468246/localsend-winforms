using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Localsend.Backend.Util
{
    /// <summary>
    /// 日志：同时写 Debug.WriteLine、进程内环形缓冲（供 UI 查看）、以及 exe 同目录下的 localsend.log。
    /// CF 3.5 真机上看不到 Debug.WriteLine，必须有文件/UI 查看通道。
    /// </summary>
    internal static class Log
    {
        private const int RingCapacity = 400;
        private static readonly object _lock = new object();
        private static readonly LinkedList<string> _ring = new LinkedList<string>();
        private static string _filePath;
        private static bool _fileInitAttempted;
        private static bool _fileEnabled; // 默认不写文件；由 UI/设置开启

        public static event EventHandler LineWritten;

        /// <summary>
        /// 是否把日志持久化到本地文件。默认 false。切为 true 时下一次 Write 会新建/截断文件。
        /// 切回 false 只是不再往文件里写，已写内容保留。
        /// </summary>
        public static bool FileLoggingEnabled
        {
            get { lock (_lock) return _fileEnabled; }
            set
            {
                lock (_lock)
                {
                    if (_fileEnabled == value) return;
                    _fileEnabled = value;
                    if (value)
                    {
                        _fileInitAttempted = false;
                        _filePath = null;
                    }
                    else
                    {
                        _filePath = null;
                        _fileInitAttempted = true; // 关闭时不尝试初始化
                    }
                }
            }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }
        public static void Error(string msg, Exception ex) { Write("ERROR", msg + ": " + ex); }

        /// <summary>返回最近 N 行的副本（旧→新）。</summary>
        public static string[] Snapshot()
        {
            lock (_lock)
            {
                string[] arr = new string[_ring.Count];
                int i = 0;
                for (LinkedListNode<string> n = _ring.First; n != null; n = n.Next) arr[i++] = n.Value;
                return arr;
            }
        }

        public static string FilePath
        {
            get { lock (_lock) { EnsureFileLocked(); return _filePath; } }
        }

        private static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + " [" + level + "] " + msg;
            Debug.WriteLine(line);

            // 整个"追加环缓冲 + 写文件"序列化，避免多线程写文件字节交错。
            lock (_lock)
            {
                _ring.AddLast(line);
                while (_ring.Count > RingCapacity) _ring.RemoveFirst();

                EnsureFileLocked();
                if (_fileEnabled && _filePath != null)
                {
                    try
                    {
                        using (FileStream fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        using (StreamWriter sw = new StreamWriter(fs, Encoding.UTF8))
                        {
                            sw.WriteLine(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        NoteFileFailureLocked("write", _filePath, ex);
                        _filePath = null;
                        _fileInitAttempted = false;
                    }
                }
            }

            EventHandler h = LineWritten;
            if (h != null) try { h(null, EventArgs.Empty); } catch { }
        }

        private static void EnsureFileLocked()
        {
            if (!_fileEnabled) return;
            if (_fileInitAttempted) return;
            _fileInitAttempted = true;

            // 候选目录：\My Documents\（WM6 保证可写）→ exe 同目录。
            List<string> candidates = new List<string>();
            try
            {
                string personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                if (!string.IsNullOrEmpty(personal)) candidates.Add(personal);
            }
            catch { }
            try
            {
                string codeBase = Assembly.GetExecutingAssembly().GetName().CodeBase;
                if (!string.IsNullOrEmpty(codeBase))
                {
                    if (codeBase.StartsWith("file:///")) codeBase = codeBase.Substring(8);
                    string exeDir = Path.GetDirectoryName(codeBase);
                    if (!string.IsNullOrEmpty(exeDir)) candidates.Add(exeDir);
                }
            }
            catch { }
            if (candidates.Count == 0) candidates.Add("\\");

            for (int i = 0; i < candidates.Count; i++)
            {
                string path = Path.Combine(candidates[i], "localsend.log");
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    using (StreamWriter sw = new StreamWriter(fs, Encoding.UTF8))
                    {
                        sw.WriteLine("--- localsend log start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ---");
                    }
                    _filePath = path;
                    NoteFileFailureLocked("open-ok", path, null);
                    return;
                }
                catch (Exception ex) { NoteFileFailureLocked("open", path, ex); }
            }
            _filePath = null;
        }

        /// <summary>必须已持有 _lock 调用。</summary>
        private static void NoteFileFailureLocked(string what, string path, Exception ex)
        {
            string msg = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " [META ] log " + what + " " + path + (ex == null ? " OK" : ": " + ex.Message);
            _ring.AddLast(msg);
            while (_ring.Count > RingCapacity) _ring.RemoveFirst();
            Debug.WriteLine(msg);
        }
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
