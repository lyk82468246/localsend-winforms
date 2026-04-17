using System;
using System.Net;
using System.Windows.Forms;

namespace Localsend
{
    /// <summary>极简输入对话框：让用户键入对端 IP 和端口用于手动探测。</summary>
    internal sealed class ProbeForm : Form
    {
        private readonly TextBox _txtIp;
        private readonly TextBox _txtPort;
        private readonly MainMenu _menu = new MainMenu();

        public IPAddress Address { get; private set; }
        public int Port { get; private set; }

        public ProbeForm()
        {
            this.Text = "Probe peer";

            Label lblIp = new Label();
            lblIp.Text = "Peer IPv4:";
            lblIp.Dock = DockStyle.Top;
            lblIp.Height = 24;
            this.Controls.Add(lblIp);

            _txtIp = new TextBox();
            _txtIp.Dock = DockStyle.Top;
            _txtIp.Text = "192.168.31.";
            this.Controls.Add(_txtIp);

            Label lblPort = new Label();
            lblPort.Text = "Port:";
            lblPort.Dock = DockStyle.Top;
            lblPort.Height = 24;
            this.Controls.Add(lblPort);

            _txtPort = new TextBox();
            _txtPort.Dock = DockStyle.Top;
            _txtPort.Text = "53317";
            this.Controls.Add(_txtPort);

            // Dock=Top 的控件按添加顺序从上到下堆叠，最后添加的在最上
            _txtPort.BringToFront();
            lblPort.BringToFront();
            _txtIp.BringToFront();
            lblIp.BringToFront();

            this.Menu = _menu;
            MenuItem ok = new MenuItem();
            ok.Text = "OK";
            ok.Click += new EventHandler(OnOk);
            _menu.MenuItems.Add(ok);

            MenuItem cancel = new MenuItem();
            cancel.Text = "Cancel";
            cancel.Click += new EventHandler(delegate(object s, EventArgs e)
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            });
            _menu.MenuItems.Add(cancel);

            this.Load += new EventHandler(delegate(object s, EventArgs e)
            {
                _txtIp.Focus();
                _txtIp.SelectionStart = _txtIp.Text.Length;
            });
        }

        private void OnOk(object sender, EventArgs e)
        {
            string ipStr = _txtIp.Text == null ? "" : _txtIp.Text.Trim();
            IPAddress ip;
            try { ip = IPAddress.Parse(ipStr); }
            catch { MessageBox.Show("Invalid IPv4"); return; }

            int port;
            try { port = int.Parse(_txtPort.Text == null ? "" : _txtPort.Text.Trim()); }
            catch { MessageBox.Show("Invalid port"); return; }
            if (port <= 0 || port > 65535) { MessageBox.Show("Port out of range"); return; }

            Address = ip;
            Port = port;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
