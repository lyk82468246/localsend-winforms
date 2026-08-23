using System;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Localsend.Backend;
using Localsend.Backend.Runtime;
using Localsend.Backend.Tls;

namespace Localsend
{
    /// <summary>
    /// Scrollable diagnostics view. A MessageBox was sufficient for the old
    /// alias/fingerprint display but cannot hold the environment and TLS
    /// probe details needed by the desktop path.
    /// </summary>
    internal sealed class AboutForm : Form
    {
        private readonly RuntimeEnvironmentInfo _environment;
        private readonly TlsProviderRouter _tls;
        private readonly string _alias;
        private readonly string _fingerprint;
        private readonly int _port;
        private readonly string _protocol;
        private readonly TextBox _text;
        private readonly MainMenu _menu;
        private readonly MenuItem _refresh;
        private readonly MenuItem _close;

        public AboutForm(
            RuntimeEnvironmentInfo environment,
            TlsProviderRouter tls,
            string alias,
            string fingerprint,
            int port,
            string protocol)
        {
            _environment = environment;
            _tls = tls;
            _alias = alias ?? "";
            _fingerprint = fingerprint ?? "";
            _port = port;
            _protocol = protocol ?? "http";

            _menu = new MainMenu();
            _refresh = new MenuItem();
            _close = new MenuItem();
            _refresh.Click += new EventHandler(OnRefresh);
            _close.Click += new EventHandler(OnClose);
            _menu.MenuItems.Add(_refresh);
            _menu.MenuItems.Add(_close);
            this.Menu = _menu;

            _text = new TextBox();
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.WordWrap = true;
            _text.ScrollBars = ScrollBars.Vertical;
            _text.Dock = DockStyle.Fill;
            this.Controls.Add(_text);

            this.Load += new EventHandler(OnLoadForm);
            this.Closed += new EventHandler(OnClosedForm);
            I18n.LanguageChanged += new EventHandler(OnLanguageChanged);
            ApplyTexts();
        }

        private void OnLoadForm(object sender, EventArgs e)
        {
            Render();
            _text.Focus();
        }

        private void OnClosedForm(object sender, EventArgs e)
        { I18n.LanguageChanged -= new EventHandler(OnLanguageChanged); }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new EventHandler(OnLanguageChanged), new object[] { sender, e }); }
                catch { }
                return;
            }
            ApplyTexts();
        }

        private void ApplyTexts()
        {
            this.Text = I18n.T("about.title");
            _refresh.Text = I18n.T("about.refresh");
            _close.Text = I18n.T("about.close");
            Render();
        }

        private void OnRefresh(object sender, EventArgs e)
        {
            try { if (_tls != null) _tls.Probe(); }
            catch { }
            Render();
        }

        private void OnClose(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        private void Render()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(I18n.T("about.application"));
            sb.AppendLine(I18n.T("about.alias", _alias));
            sb.AppendLine(I18n.T("about.fingerprint", _fingerprint));
            sb.AppendLine(I18n.T("about.port", _port));
            sb.AppendLine(I18n.T("about.protocol", _protocol));
            sb.AppendLine();
            sb.AppendLine(I18n.T("about.environment"));

            if (_environment != null)
            {
                sb.AppendLine(I18n.T("about.appVersion", _environment.ApplicationVersion));
                sb.AppendLine(I18n.T("about.os", _environment.OperatingSystem));
                sb.AppendLine(I18n.T("about.osVersion", _environment.OperatingSystemVersion));
                sb.AppendLine(I18n.T("about.osBuild", _environment.OperatingSystemBuild));
                sb.AppendLine(I18n.T("about.processArch", _environment.ProcessArchitecture));
                sb.AppendLine(I18n.T("about.nativeArch", _environment.NativeArchitecture));
                sb.AppendLine(I18n.T("about.pointerBits", _environment.PointerBits));
                sb.AppendLine(I18n.T("about.framework", _environment.FrameworkKind, _environment.RuntimeVersion));
            }

            sb.AppendLine();
            sb.AppendLine(I18n.T("about.encryption"));
            EncryptionCapabilityReport report = _tls == null
                ? EncryptionCapabilityReport.NotProbed()
                : _tls.Report;
            sb.AppendLine(I18n.T("about.provider", report.Provider));
            sb.AppendLine(I18n.T("about.capability", CapabilityText(report.Level)));
            sb.AppendLine(I18n.T("about.transport", _protocol == "https"
                && _tls != null && _tls.SupportsStreamTransport
                ? I18n.T("about.transportReady") : I18n.T("about.transportUnavailable")));
            sb.AppendLine(I18n.T("about.reason", I18n.T(report.ReasonCode)));
            if (!string.IsNullOrEmpty(report.Detail))
                sb.AppendLine(I18n.T("about.detail", report.Detail));
            sb.AppendLine(I18n.T("about.checked", report.TestedAtUtc.ToString("u", CultureInfo.InvariantCulture)));

            _text.Text = sb.ToString();
            try { _text.SelectionStart = 0; _text.ScrollToCaret(); } catch { }
        }

        private static string CapabilityText(EncryptionCapabilityLevel level)
        {
            switch (level)
            {
                case EncryptionCapabilityLevel.ReceiveOnly: return I18n.T("tls.capability.receiveOnly");
                case EncryptionCapabilityLevel.Full: return I18n.T("tls.capability.full");
                default: return I18n.T("tls.capability.unavailable");
            }
        }
    }
}
