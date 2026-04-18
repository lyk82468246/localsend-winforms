using System;
using System.Collections.Generic;
using System.Globalization;
using Localsend.Backend.Util;

namespace Localsend.Backend.Protocol
{
    /// <summary>设备信息（announce / info / send-request.info 共用）。</summary>
    internal sealed class DeviceInfo
    {
        public string Alias;
        public string DeviceModel;
        public string DeviceType;
        public string Fingerprint;
        public string Version;
        public int Port;
        public string Protocol;
        public bool Download;

        public Dictionary<string, object> ToJson(bool includeV2Fields)
        {
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["alias"] = Alias;
            o["deviceModel"] = DeviceModel;
            o["deviceType"] = DeviceType;
            o["fingerprint"] = Fingerprint;
            if (includeV2Fields)
            {
                o["version"] = Version;
                o["port"] = (long)Port;
                o["protocol"] = Protocol;
                o["download"] = Download;
            }
            return o;
        }

        public static DeviceInfo FromJson(Dictionary<string, object> o)
        {
            DeviceInfo d = new DeviceInfo();
            d.Alias = JsonHelpers.AsString(o, "alias");
            d.DeviceModel = JsonHelpers.AsString(o, "deviceModel");
            d.DeviceType = JsonHelpers.AsString(o, "deviceType");
            d.Fingerprint = JsonHelpers.AsString(o, "fingerprint");
            d.Version = JsonHelpers.AsString(o, "version");
            d.Port = (int)JsonHelpers.AsLong(o, "port", Constants.RestPort);
            d.Protocol = JsonHelpers.AsString(o, "protocol");
            d.Download = JsonHelpers.AsBool(o, "download", false);
            return d;
        }
    }

    /// <summary>UDP 多播 announce 消息。本端只实现 v1 接收端，announce 按 v1 形状发出
    /// （不含 version/port/protocol/download），以免 v2 客户端对我们使用 /v2/ 端点。</summary>
    internal sealed class AnnounceMessage
    {
        public DeviceInfo Info;
        public bool Announcement;

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> o = Info.ToJson(false);
            o["announcement"] = Announcement;
            return o;
        }

        public static AnnounceMessage FromJson(Dictionary<string, object> o)
        {
            AnnounceMessage m = new AnnounceMessage();
            m.Info = DeviceInfo.FromJson(o);
            m.Announcement = JsonHelpers.AsBool(o, "announcement", true);
            return m;
        }
    }

    /// <summary>send-request.files 中的单个文件条目。</summary>
    internal sealed class FileDto
    {
        public string Id;
        public string FileName;
        public long Size;
        public string FileType; // image | video | pdf | text | other
        public string Preview;

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["id"] = Id;
            o["fileName"] = FileName;
            o["size"] = Size;
            o["fileType"] = FileType;
            o["preview"] = Preview; // may be null
            return o;
        }

        public static FileDto FromJson(Dictionary<string, object> o)
        {
            FileDto f = new FileDto();
            f.Id = JsonHelpers.AsString(o, "id");
            f.FileName = JsonHelpers.AsString(o, "fileName");
            f.Size = JsonHelpers.AsLong(o, "size", 0);
            f.FileType = JsonHelpers.AsString(o, "fileType");
            f.Preview = JsonHelpers.AsString(o, "preview");
            return f;
        }
    }

    /// <summary>POST /send-request 请求体。</summary>
    internal sealed class SendRequest
    {
        public DeviceInfo Info;
        public Dictionary<string, FileDto> Files;

        public static SendRequest FromJson(Dictionary<string, object> root)
        {
            SendRequest r = new SendRequest();
            Dictionary<string, object> infoObj = root.ContainsKey("info") ? root["info"] as Dictionary<string, object> : null;
            r.Info = infoObj != null ? DeviceInfo.FromJson(infoObj) : new DeviceInfo();

            r.Files = new Dictionary<string, FileDto>();
            Dictionary<string, object> filesObj = root.ContainsKey("files") ? root["files"] as Dictionary<string, object> : null;
            if (filesObj != null)
            {
                foreach (KeyValuePair<string, object> kv in filesObj)
                {
                    Dictionary<string, object> fobj = kv.Value as Dictionary<string, object>;
                    if (fobj != null)
                        r.Files[kv.Key] = FileDto.FromJson(fobj);
                }
            }
            return r;
        }
    }

    /// <summary>JSON 字典安全取值辅助。</summary>
    internal static class JsonHelpers
    {
        public static string AsString(Dictionary<string, object> o, string key)
        {
            if (o == null || !o.ContainsKey(key)) return null;
            object v = o[key];
            return v == null ? null : v.ToString();
        }

        public static long AsLong(Dictionary<string, object> o, string key, long def)
        {
            if (o == null || !o.ContainsKey(key) || o[key] == null) return def;
            object v = o[key];
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            long r;
            return Parse.TryLong(v.ToString(), out r) ? r : def;
        }

        public static bool AsBool(Dictionary<string, object> o, string key, bool def)
        {
            if (o == null || !o.ContainsKey(key) || o[key] == null) return def;
            object v = o[key];
            if (v is bool) return (bool)v;
            bool r;
            return Parse.TryBool(v.ToString(), out r) ? r : def;
        }
    }
}
