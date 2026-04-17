using System;
using System.Net;
using System.Windows.Forms;

namespace Localsend
{
    /// <summary>极简输入对话框：让用户键入对端 IP 用于手动探测。</summary>
    internal sealed class ProbeForm : Form
    {
        private readonly TextBox _txt;
        private readonly MainMenu _menu = new MainMenu();

        public IPAddress Address { get; private set; }

        public ProbeForm()
        {
            this.Text = "Probe peer";

            Label lbl = new Label();
            lbl.Text = "Peer IPv4:";
            lbl.Dock = DockStyle.Top;
            lbl.Height = 24;
            this.Controls.Add(lbl);

            _txt = new TextBox();
            _txt.Dock = DockStyle.Top;
            _txt.Text = "192.168.31.";
            this.Controls.Add(_txt);
            _txt.BringToFront();

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

            this.Load += new EventHandler(delegate(object s, EventArgs e) { _txt.Focus(); _txt.SelectionStart = _txt.Text.Length; });
        }

        private void OnOk(object sender, EventArgs e)
        {
            string s = _txt.Text == null ? "" : _txt.Text.Trim();
            IPAddress ip;
            try { ip = IPAddress.Parse(s); }
            catch { MessageBox.Show("Invalid IPv4"); return; }
            Address = ip;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
