using System;
using System.Text;
using System.Windows.Forms;
using Localsend.Backend.Util;

namespace Localsend
{
    /// <summary>简易日志查看器。显示 Log 环形缓冲，实时追加，并显示日志文件路径。</summary>
    internal sealed class LogForm : Form
    {
        private readonly TextBox _txt;
        private readonly Label _lblPath;
        private readonly MainMenu _menu = new MainMenu();

        public LogForm()
        {
            this.Text = "Log";
            this.Menu = _menu;

            _lblPath = new Label();
            _lblPath.Dock = DockStyle.Top;
            _lblPath.Height = 32;
            _lblPath.Text = Log.FilePath ?? "(no file)";
            this.Controls.Add(_lblPath);

            _txt = new TextBox();
            _txt.Multiline = true;
            _txt.ReadOnly = true;
            _txt.ScrollBars = ScrollBars.Both;
            _txt.WordWrap = false;
            _txt.Dock = DockStyle.Fill;
            this.Controls.Add(_txt);
            _txt.BringToFront();

            MenuItem miRefresh = new MenuItem();
            miRefresh.Text = "Refresh";
            miRefresh.Click += new EventHandler(delegate(object s, EventArgs e) { Reload(); });
            _menu.MenuItems.Add(miRefresh);

            MenuItem miClose = new MenuItem();
            miClose.Text = "Close";
            miClose.Click += new EventHandler(delegate(object s, EventArgs e) { this.Close(); });
            _menu.MenuItems.Add(miClose);

            this.Load += new EventHandler(delegate(object s, EventArgs e) { Reload(); });
            Log.LineWritten += OnLogLine;
            this.Closed += new EventHandler(delegate(object s, EventArgs e) { Log.LineWritten -= OnLogLine; });
        }

        private void OnLogLine(object sender, EventArgs e)
        {
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new EventHandler(OnLogLine), new object[] { sender, e }); } catch { }
                return;
            }
            Reload();
        }

        private void Reload()
        {
            string[] lines = Log.Snapshot();
            StringBuilder sb = new StringBuilder(lines.Length * 80);
            for (int i = 0; i < lines.Length; i++) sb.Append(lines[i]).Append("\r\n");
            _txt.Text = sb.ToString();
            try { _txt.SelectionStart = _txt.Text.Length; _txt.ScrollToCaret(); } catch { }
        }
    }
}
