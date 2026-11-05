using System;
using System.Drawing;
using System.Windows.Forms;

namespace EveLauncher
{
    /// <summary>首次运行索取 FTP 密码用的小弹窗。密码不落明文，输入后由调用方 DPAPI 加密。</summary>
    internal static class PasswordPrompt
    {
        public static string Ask(string caption)
        {
            using (var f = new Form())
            {
                f.Text = "EveLauncher";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ClientSize = new Size(420, 140);
                f.TopMost = true;

                var lbl = new Label
                {
                    Text = caption,
                    AutoSize = false,
                    Location = new Point(16, 16),
                    Size = new Size(388, 40)
                };

                var txt = new TextBox
                {
                    Location = new Point(16, 60),
                    Width = 388,
                    UseSystemPasswordChar = true
                };

                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(232, 98), Width = 80 };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(324, 98), Width = 80 };

                f.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                return f.ShowDialog() == DialogResult.OK ? txt.Text : null;
            }
        }
    }
}
