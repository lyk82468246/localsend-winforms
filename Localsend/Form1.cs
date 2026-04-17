using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Localsend.Backend;
using Localsend.Backend.Receiver;
using Localsend.Backend.Sender;

namespace Localsend
{
    public partial class Form1 : Form
    {
        private AppSettings _cfg;
        private LocalSendService _svc;

        private Label _lblSelf;
        private ListBox _listPeers;
        private StatusBar _status;
        private MenuItem _miSend;
        private MenuItem _miMenu;
        private MenuItem _miRefresh;
        private MenuItem _miLang;
        private MenuItem _miLangEn;
        private MenuItem _miLangZh;
        private MenuItem _miAbout;
        private MenuItem _miExit;

        public Form1()
        {
            InitializeComponent();
            BuildUi();
            this.Load += new EventHandler(OnLoad);
            this.Closed += new EventHandler(OnClosedForm);
            I18n.LanguageChanged += new EventHandler(OnLanguageChanged);
        }

        private void BuildUi()
        {
            _lblSelf = new Label();
            _lblSelf.Dock = DockStyle.Top;
            _lblSelf.Height = 32;
            this.Controls.Add(_lblSelf);

            _listPeers = new ListBox();
            _listPeers.Dock = DockStyle.Fill;
            this.Controls.Add(_listPeers);
            _listPeers.BringToFront();

            _status = new StatusBar();
            this.Controls.Add(_status);

            _miSend = new MenuItem();
            _miSend.Click += new EventHandler(OnSendClick);

            _miMenu = new MenuItem();
            _miRefresh = new MenuItem(); _miRefresh.Click += new EventHandler(OnRefreshClick);
            _miLang = new MenuItem();
            _miLangEn = new MenuItem(); _miLangEn.Click += new EventHandler(OnLangEnClick);
            _miLangZh = new MenuItem(); _miLangZh.Click += new EventHandler(OnLangZhClick);
            _miLang.MenuItems.Add(_miLangEn);
            _miLang.MenuItems.Add(_miLangZh);
            _miAbout = new MenuItem(); _miAbout.Click += new EventHandler(OnAboutClick);
            _miExit = new MenuItem(); _miExit.Click += new EventHandler(OnExitClick);
            _miMenu.MenuItems.Add(_miRefresh);
            _miMenu.MenuItems.Add(_miLang);
            _miMenu.MenuItems.Add(_miAbout);
            _miMenu.MenuItems.Add(_miExit);

            this.mainMenu1.MenuItems.Add(_miSend);
            this.mainMenu1.MenuItems.Add(_miMenu);

            ApplyTexts();
        }

        private void ApplyTexts()
        {
            this.Text = I18n.T("app.title");
            _miSend.Text = I18n.T("menu.send");
            _miMenu.Text = I18n.T("menu.more");
            _miRefresh.Text = I18n.T("menu.refresh");
            _miLang.Text = I18n.T("menu.language");
            _miLangEn.Text = I18n.T("menu.lang.en") + (I18n.Current == I18n.LangEn ? " *" : "");
            _miLangZh.Text = I18n.T("menu.lang.zh") + (I18n.Current == I18n.LangZh ? " *" : "");
            _miAbout.Text = I18n.T("menu.about");
            _miExit.Text = I18n.T("menu.exit");

            if (_cfg != null)
                _lblSelf.Text = I18n.T("label.self", _cfg.Alias, _cfg.DownloadDir);
            else
                _lblSelf.Text = I18n.T("status.starting");

            if (_svc != null)
                _status.Text = I18n.T("status.listening", Localsend.Backend.Protocol.Constants.RestPort);
            else
                _status.Text = I18n.T("status.ready");
        }

        private void OnLoad(object sender, EventArgs e)
        {
            try
            {
                _cfg = AppSettings.LoadOrCreate();
                if (!string.IsNullOrEmpty(_cfg.Language)) I18n.Current = _cfg.Language;

                _svc = new LocalSendService(_cfg.Alias, _cfg.DownloadDir, null, _cfg.Fingerprint);
                _svc.Peers.PeerListChanged += new EventHandler(OnPeerListChanged);
                _svc.Sender.Progress += new EventHandler<SendProgressEventArgs>(OnSendProgress);
                _svc.Start();

                ApplyTexts();
                RefreshPeerList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.startFailed", ex.Message));
            }
        }

        private void OnClosedForm(object sender, EventArgs e)
        {
            try { if (_svc != null) _svc.Stop(); } catch { }
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (this.InvokeRequired) { this.BeginInvoke(new EventHandler(OnLanguageChanged), new object[] { sender, e }); return; }
            ApplyTexts();
        }

        // ---- menu ----

        private void OnSendClick(object sender, EventArgs e)
        {
            if (_listPeers.SelectedIndex < 0 || !(_listPeers.SelectedItem is PeerItem))
            { MessageBox.Show(I18n.T("msg.selectPeer")); return; }
            Peer peer = ((PeerItem)_listPeers.SelectedItem).Peer;

            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = I18n.T("msg.filter");
            if (ofd.ShowDialog() != DialogResult.OK) return;
            if (string.IsNullOrEmpty(ofd.FileName) || !File.Exists(ofd.FileName)) return;

            try
            {
                SenderFileSpec spec = SenderFileSpec.FromPath(ofd.FileName);
                _svc.Sender.Send(peer, new SenderFileSpec[] { spec });
                _status.Text = I18n.T("status.sending", spec.FileName);
            }
            catch (Exception ex) { MessageBox.Show(I18n.T("msg.sendFailed", ex.Message)); }
        }

        private void OnRefreshClick(object sender, EventArgs e) { RefreshPeerList(); }

        private void OnLangEnClick(object sender, EventArgs e) { SetLang(I18n.LangEn); }
        private void OnLangZhClick(object sender, EventArgs e) { SetLang(I18n.LangZh); }

        private void SetLang(string lang)
        {
            I18n.Current = lang;
            if (_cfg != null) { _cfg.Language = lang; _cfg.Save(); }
        }

        private void OnAboutClick(object sender, EventArgs e)
        {
            string alias = _cfg != null ? _cfg.Alias : "";
            string fp = _cfg != null ? _cfg.Fingerprint : "";
            MessageBox.Show(I18n.T("about.body", alias, fp));
        }

        private void OnExitClick(object sender, EventArgs e) { this.Close(); }

        // ---- events from background ----

        private void OnPeerListChanged(object sender, EventArgs e)
        {
            if (this.InvokeRequired) { this.BeginInvoke(new EventHandler(OnPeerListChanged), new object[] { sender, e }); return; }
            RefreshPeerList();
        }

        private void OnSendProgress(object sender, SendProgressEventArgs e)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new EventHandler<SendProgressEventArgs>(OnSendProgress), new object[] { sender, e });
                return;
            }
            switch (e.Stage)
            {
                case SendStage.Preparing: _status.Text = I18n.T("status.preparing"); break;
                case SendStage.Uploading:
                    int pct = e.TotalBytes > 0 ? (int)(100L * e.BytesSent / e.TotalBytes) : 0;
                    _status.Text = I18n.T("status.uploading", e.FileName, pct);
                    break;
                case SendStage.FileDone: _status.Text = I18n.T("status.fileDone", e.FileName); break;
                case SendStage.JobDone: _status.Text = I18n.T("status.jobDone"); break;
                case SendStage.Rejected: MessageBox.Show(I18n.T("msg.peerRejected", e.Message)); _status.Text = I18n.T("status.rejected"); break;
                case SendStage.Cancelled: _status.Text = I18n.T("status.cancelled"); break;
                case SendStage.Failed: MessageBox.Show(I18n.T("msg.sendFailed", e.Message)); _status.Text = I18n.T("status.failed"); break;
            }
        }

        private void RefreshPeerList()
        {
            object selFp = null;
            if (_listPeers.SelectedItem is PeerItem) selFp = ((PeerItem)_listPeers.SelectedItem).Peer.Fingerprint;

            _listPeers.BeginUpdate();
            try
            {
                _listPeers.Items.Clear();
                List<Peer> snap = _svc != null ? _svc.Peers.Snapshot() : new List<Peer>();
                int restore = -1;
                for (int i = 0; i < snap.Count; i++)
                {
                    PeerItem pi = new PeerItem(snap[i]);
                    _listPeers.Items.Add(pi);
                    if (selFp != null && snap[i].Fingerprint == (string)selFp) restore = i;
                }
                if (restore >= 0) _listPeers.SelectedIndex = restore;
            }
            finally { _listPeers.EndUpdate(); }
        }

        private sealed class PeerItem
        {
            public readonly Peer Peer;
            public PeerItem(Peer p) { Peer = p; }
            public override string ToString()
            {
                string scheme = Peer.Protocol == "https" ? " (https)" : "";
                return Peer.Alias + "  [" + Peer.Address + "]" + scheme;
            }
        }
    }
}
