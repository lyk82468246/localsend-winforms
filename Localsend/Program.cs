using System;
using System.Windows.Forms;

namespace Localsend
{
    static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [MTAThread]
        static void Main()
        {
            try
            {
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                // Keep startup/runtime failures from becoming the generic
                // CLR 0xe0434352 application-error dialog.  TLS probing and
                // optional Positron loading are best-effort; if a different
                // unexpected error escapes the UI loop, report it plainly.
                try { MessageBox.Show("LocalSend startup failed: " + ex.Message); }
                catch { }
            }
        }
    }
}
