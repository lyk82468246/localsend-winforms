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

        public static string T(string key, params object[] args)
        { return string.Format(CultureInfo.InvariantCulture, T(key), args); }

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
            en["app.title"] = "LocalSend";
            zh["app.title"] = "LocalSend";

            // menu
            en["menu.send"] = "Send"; zh["menu.send"] = "发送";
            en["menu.more"] = "Menu"; zh["menu.more"] = "菜单";
            en["menu.refresh"] = "Refresh"; zh["menu.refresh"] = "刷新";
            en["menu.about"] = "About"; zh["menu.about"] = "关于";
            en["menu.log"] = "Log"; zh["menu.log"] = "日志";
            en["menu.logFile"] = "Log to file"; zh["menu.logFile"] = "持久日志";
            en["menu.probe"] = "Probe..."; zh["menu.probe"] = "探测...";
            en["menu.exit"] = "Exit"; zh["menu.exit"] = "退出";
            en["menu.language"] = "Language"; zh["menu.language"] = "语言";
            en["menu.lang.en"] = "English"; zh["menu.lang.en"] = "English";
            en["menu.lang.zh"] = "Chinese"; zh["menu.lang.zh"] = "中文";

            // labels
            en["label.self"] = "Me: {0}\r\nSave to: {1}";
            zh["label.self"] = "我: {0}\r\n保存到: {1}";

            // status
            en["status.starting"] = "starting..."; zh["status.starting"] = "启动中...";
            en["status.listening"] = "Listening on {0}://:{1}"; zh["status.listening"] = "监听中 {0}://:{1}";
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
            en["about.title"] = "About LocalSend"; zh["about.title"] = "关于 LocalSend";
            en["about.application"] = "Application: LocalSend WinForms v1.0"; zh["about.application"] = "应用：LocalSend WinForms v1.0";
            en["about.alias"] = "Alias: {0}"; zh["about.alias"] = "别名：{0}";
            en["about.fingerprint"] = "Device fingerprint: {0}"; zh["about.fingerprint"] = "设备指纹：{0}";
            en["about.port"] = "Port: {0}"; zh["about.port"] = "端口：{0}";
            en["about.protocol"] = "Advertised protocol: {0}"; zh["about.protocol"] = "宣告协议：{0}";
            en["about.environment"] = "Runtime environment"; zh["about.environment"] = "运行环境";
            en["about.appVersion"] = "Assembly version: {0}"; zh["about.appVersion"] = "程序集版本：{0}";
            en["about.os"] = "Operating system: {0}"; zh["about.os"] = "操作系统：{0}";
            en["about.osVersion"] = "OS version: {0}"; zh["about.osVersion"] = "系统版本：{0}";
            en["about.osBuild"] = "OS build: {0}"; zh["about.osBuild"] = "系统 Build：{0}";
            en["about.processArch"] = "Process CPU: {0}"; zh["about.processArch"] = "进程 CPU：{0}";
            en["about.nativeArch"] = "Native CPU: {0}"; zh["about.nativeArch"] = "系统 CPU：{0}";
            en["about.pointerBits"] = "Pointer width: {0}"; zh["about.pointerBits"] = "指针宽度：{0}";
            en["about.framework"] = "Framework: {0} {1}"; zh["about.framework"] = "框架：{0} {1}";
            en["about.encryption"] = "Encryption capability"; zh["about.encryption"] = "加密能力";
            en["about.provider"] = "TLS provider: {0}"; zh["about.provider"] = "TLS 提供者：{0}";
            en["about.capability"] = "Status: {0}"; zh["about.capability"] = "状态：{0}";
            en["about.transport"] = "HTTP TLS transport: {0}"; zh["about.transport"] = "HTTP TLS 传输：{0}";
            en["about.transportReady"] = "available"; zh["about.transportReady"] = "可用";
            en["about.transportUnavailable"] = "not connected"; zh["about.transportUnavailable"] = "尚未接入";
            en["about.reason"] = "Reason: {0}"; zh["about.reason"] = "原因：{0}";
            en["about.detail"] = "Diagnostic detail: {0}"; zh["about.detail"] = "诊断详情：{0}";
            en["about.checked"] = "Last checked (UTC): {0}"; zh["about.checked"] = "最近探测（UTC）：{0}";
            en["about.refresh"] = "Re-probe"; zh["about.refresh"] = "重新探测";
            en["about.close"] = "Close"; zh["about.close"] = "关闭";
            en["tls.capability.unavailable"] = "No encryption"; zh["tls.capability.unavailable"] = "完全无法加密";
            en["tls.capability.receiveOnly"] = "May receive encrypted sends only"; zh["tls.capability.receiveOnly"] = "可能仅接受官方客户端的加密发送";
            en["tls.capability.full"] = "Full encryption"; zh["tls.capability.full"] = "完全可以加密";
            en["tls.providerPending"] = "The platform TLS provider is not connected yet."; zh["tls.providerPending"] = "平台 TLS 提供者尚未接入。";
            en["tls.notProbed"] = "TLS capability has not been probed."; zh["tls.notProbed"] = "尚未探测 TLS 加密能力。";
            en["tls.probeFailed"] = "TLS capability probe failed."; zh["tls.probeFailed"] = "TLS 加密能力探测失败。";
            en["tls.providerDisposed"] = "TLS provider has been closed."; zh["tls.providerDisposed"] = "TLS 提供者已关闭。";
            en["tls.systemApiMissing"] = "Windows Schannel API is unavailable."; zh["tls.systemApiMissing"] = "Windows Schannel API 不可用。";
            en["tls.desktopOnly"] = "The Schannel provider is desktop-only."; zh["tls.desktopOnly"] = "Schannel 提供者仅适用于桌面 Windows。";
            en["tls.ceOnly"] = "The Positron provider is Windows CE-only."; zh["tls.ceOnly"] = "Positron 提供者仅适用于 Windows CE。";
            en["tls.ok"] = "TLS 1.2 and mutual certificate authentication are available."; zh["tls.ok"] = "TLS 1.2 与双向证书认证可用。";
            en["tls.handshakeFailed"] = "TLS handshake failed."; zh["tls.handshakeFailed"] = "TLS 握手失败。";
            en["tls.mutualAuthUnavailable"] = "TLS works, but mutual certificate authentication is unavailable."; zh["tls.mutualAuthUnavailable"] = "TLS 可用，但双向证书认证不可用。";
            en["tls.positronDllMissing"] = "positron_tls.dll was not found or has the wrong processor architecture."; zh["tls.positronDllMissing"] = "找不到 positron_tls.dll，或处理器架构不匹配。";
            en["tls.positronAbiInvalid"] = "positron_tls.dll does not expose a compatible ABI."; zh["tls.positronAbiInvalid"] = "positron_tls.dll 没有兼容的 ABI。";
            en["tls.positronInitFailed"] = "Positron TLS initialization failed."; zh["tls.positronInitFailed"] = "Positron TLS 初始化失败。";
            en["tls.positronAbiLegacy"] = "Only the legacy Positron TLS ABI was found; peer mTLS is unavailable."; zh["tls.positronAbiLegacy"] = "仅发现旧版 Positron TLS ABI，无法使用对等端双向认证。";

            en["about.body"] = "LocalSend WinForms v1.0\r\nAlias: {0}\r\nFingerprint: {1}\r\nPort: 53317";
            zh["about.body"] = "LocalSend WinForms v1.0\r\n别名: {0}\r\n指纹: {1}\r\n端口: 53317";

            _dicts[LangEn] = en;
            _dicts[LangZh] = zh;
        }
    }
}
