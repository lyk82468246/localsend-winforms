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

        private string _path;

        public static AppSettings LoadOrCreate()
        {
            string dir = @"\My Documents\LocalSend";
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
                }
                catch (Exception ex) { Log.Warn("settings load failed: " + ex.Message); }
            }

            if (string.IsNullOrEmpty(s.Alias)) s.Alias = "WM6-" + IdGen.NewRandom().Substring(0, 4);
            if (string.IsNullOrEmpty(s.DownloadDir)) s.DownloadDir = dir;
            if (string.IsNullOrEmpty(s.Fingerprint)) s.Fingerprint = IdGen.NewRandom();

            s.Save();
            return s;
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
                using (StreamWriter w = new StreamWriter(_path, false, Encoding.UTF8))
                    w.Write(Json.Stringify(o));
            }
            catch (Exception ex) { Log.Warn("settings save failed: " + ex.Message); }
        }
    }
}
