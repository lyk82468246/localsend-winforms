using System;
using System.Collections.Generic;
using System.Globalization;

namespace Localsend.Backend
{
    /// <summary>轻量运行时 i18n。两套词典，菜单可切换，选择持久化到 AppSettings。</summary>
    public static class I18n
    {
        public const string LangEn = "en";
        public const string LangZh = "zh";

        private static string _current;
        private static readonly Dictionary<string, Dictionary<string, string>> _dicts
            = new Dictionary<string, Dictionary<string, string>>();

        public static event EventHandler LanguageChanged;

        static I18n()
        {
            Init();
            _current = DetectDefault();
        }

        public static string Current
        {
            get { return _current; }
            set
            {
                string v = (value == LangZh) ? LangZh : LangEn;
                if (v == _current) return;
                _current = v;
                EventHandler h = LanguageChanged;
                if (h != null) try { h(null, EventArgs.Empty); } catch { }
            }
        }

        /// <summary>查表；未命中则回落到英文，再回落到 key 本身。</summary>
        public static string T(string key)
        {
            string v;
            Dictionary<string, string> d;
            if (_dicts.TryGetValue(_current, out d) && d.TryGetValue(key, out v)) return v;
            if (_dicts.TryGetValue(LangEn, out d) && d.TryGetValue(key, out v)) return v;
            return key;
        }

        public static string T(string key, object a)
        { return string.Format(CultureInfo.InvariantCulture, T(key), a); }

        public static string T(string key, object a, object b)
        { return string.Format(CultureInfo.InvariantCulture, T(key), a, b); }

        private static string DetectDefault()
        {
            try
            {
                string name = CultureInfo.CurrentCulture.Name;
                if (!string.IsNullOrEmpty(name) && name.ToLower().StartsWith("zh")) return LangZh;
            }
            catch { }
            return LangEn;
        }

        private static void Init()
        {
            Dictionary<string, string> en = new Dictionary<string, string>();
            Dictionary<string, string> zh = new Dictionary<string, string>();

            // window title
            en["app.title"] = "LocalSend (WM)";
            zh["app.title"] = "LocalSend (WM)";

            // menu
            en["menu.send"] = "Send"; zh["menu.send"] = "发送";
            en["menu.more"] = "Menu"; zh["menu.more"] = "菜单";
            en["menu.refresh"] = "Refresh"; zh["menu.refresh"] = "刷新";
            en["menu.about"] = "About"; zh["menu.about"] = "关于";
            en["menu.exit"] = "Exit"; zh["menu.exit"] = "退出";
            en["menu.language"] = "Language"; zh["menu.language"] = "语言";
            en["menu.lang.en"] = "English"; zh["menu.lang.en"] = "English";
            en["menu.lang.zh"] = "Chinese"; zh["menu.lang.zh"] = "中文";

            // labels
            en["label.self"] = "Me: {0}\r\nSave to: {1}";
            zh["label.self"] = "我: {0}\r\n保存到: {1}";

            // status
            en["status.starting"] = "starting..."; zh["status.starting"] = "启动中...";
            en["status.listening"] = "Listening on :{0}"; zh["status.listening"] = "监听中 :{0}";
            en["status.ready"] = "Ready"; zh["status.ready"] = "就绪";
            en["status.preparing"] = "Preparing..."; zh["status.preparing"] = "准备...";
            en["status.uploading"] = "Uploading {0} {1}%"; zh["status.uploading"] = "上传 {0} {1}%";
            en["status.fileDone"] = "Done {0}"; zh["status.fileDone"] = "完成 {0}";
            en["status.jobDone"] = "All complete"; zh["status.jobDone"] = "全部完成";
            en["status.rejected"] = "Rejected"; zh["status.rejected"] = "被拒绝";
            en["status.cancelled"] = "Cancelled"; zh["status.cancelled"] = "已取消";
            en["status.failed"] = "Failed"; zh["status.failed"] = "失败";
            en["status.sending"] = "Sending: {0}"; zh["status.sending"] = "发送中: {0}";

            // messages
            en["msg.selectPeer"] = "Please select a peer in the list first.";
            zh["msg.selectPeer"] = "请先在列表中选中一个对端";
            en["msg.startFailed"] = "Start failed: {0}"; zh["msg.startFailed"] = "启动失败: {0}";
            en["msg.sendFailed"] = "Send failed: {0}"; zh["msg.sendFailed"] = "发送失败: {0}";
            en["msg.peerRejected"] = "Peer rejected: {0}"; zh["msg.peerRejected"] = "对端拒绝: {0}";
            en["msg.filter"] = "All files|*.*"; zh["msg.filter"] = "所有文件|*.*";

            // about dialog
            en["about.body"] = "LocalSend for WM6\r\nAlias: {0}\r\nFingerprint: {1}\r\nPort: 53317";
            zh["about.body"] = "LocalSend for WM6\r\n别名: {0}\r\n指纹: {1}\r\n端口: 53317";

            _dicts[LangEn] = en;
            _dicts[LangZh] = zh;
        }
    }
}
