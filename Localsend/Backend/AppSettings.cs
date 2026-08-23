using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Localsend.Backend.Protocol;
using Localsend.Backend.Util;

namespace Localsend.Backend
{
    /// <summary>应用设置，首次运行写入 config.json 并持久化指纹。</summary>
    public sealed class AppSettings
    {
        public string Alias;
        public string DownloadDir;
        public string Fingerprint;
        public string Language;
        public bool LogToFile;

        private string _path;

        public static AppSettings LoadOrCreate()
        {
            string dir = GetDefaultDataDirectory();
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            string path = Path.Combine(dir, "config.json");

            AppSettings s = new AppSettings();
            s._path = path;

            if (File.Exists(path))
            {
                try
                {
                    string text;
                    using (StreamReader r = new StreamReader(path, Encoding.UTF8)) text = r.ReadToEnd();
                    Dictionary<string, object> o = Json.ParseObject(text);
                    s.Alias = JsonHelpers.AsString(o, "alias");
                    s.DownloadDir = JsonHelpers.AsString(o, "downloadDir");
                    s.Fingerprint = JsonHelpers.AsString(o, "fingerprint");
                    s.Language = JsonHelpers.AsString(o, "language");
                    s.LogToFile = JsonHelpers.AsBool(o, "logToFile", false);
                }
                catch (Exception ex) { Log.Warn("settings load failed: " + ex.Message); }
            }

            if (string.IsNullOrEmpty(s.Alias))
            {
                bool ce = false;
                try { ce = Environment.OSVersion.Platform == PlatformID.WinCE; } catch { }
                s.Alias = (ce ? "WM6-" : "LocalSend-") + IdGen.NewRandom().Substring(0, 4);
            }
            if (string.IsNullOrEmpty(s.DownloadDir)) s.DownloadDir = dir;
            if (string.IsNullOrEmpty(s.Fingerprint)) s.Fingerprint = IdGen.NewRandom();

            s.Save();
            return s;
        }

        private static string GetDefaultDataDirectory()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.WinCE)
                    return @"\My Documents\LocalSend";
            }
            catch { }

            // ApplicationData is available on both the desktop CLR and CF;
            // use it for PC persistence and keep a current-directory fallback
            // for locked-down or portable installations.
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                    return Path.Combine(appData, "LocalSend-WinForms");
            }
            catch { }
            try { return Path.Combine(Directory.GetCurrentDirectory(), "LocalSendData"); }
            catch { return @"\My Documents\LocalSend"; }
        }

        public void Save()
        {
            try
            {
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["alias"] = Alias;
                o["downloadDir"] = DownloadDir;
                o["fingerprint"] = Fingerprint;
                o["language"] = Language;
                o["logToFile"] = LogToFile;
                using (StreamWriter w = new StreamWriter(_path, false, Encoding.UTF8))
                    w.Write(Json.Stringify(o));
            }
            catch (Exception ex) { Log.Warn("settings save failed: " + ex.Message); }
        }
    }
}
