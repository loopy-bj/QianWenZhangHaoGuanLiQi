using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace QianwenSwitcher.UI
{
    /// <summary>统一的备注名输入对话框</summary>
    public class InputDialog : Form
    {
        private readonly TextBox _textBox;
        private readonly Label _label;

        public string InputText { get { return _textBox.Text.Trim(); } }

        private InputDialog(string title, string prompt, string defaultValue)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(400, 150);
            Font = new Font("Microsoft YaHei UI", 9F);

            _label = new Label();
            _label.Text = prompt;
            _label.Left = 14;
            _label.Top = 14;
            _label.Width = 372;
            _label.Height = 20;

            _textBox = new TextBox();
            _textBox.Left = 14;
            _textBox.Top = 42;
            _textBox.Width = 372;
            _textBox.Text = defaultValue ?? string.Empty;

            Button ok = new Button();
            ok.Text = "确定";
            ok.Width = 90;
            ok.Height = 30;
            ok.Left = 196;
            ok.Top = 96;
            ok.DialogResult = DialogResult.OK;

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Width = 90;
            cancel.Height = 30;
            cancel.Left = 296;
            cancel.Top = 96;
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(_label);
            Controls.Add(_textBox);
            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            Load += delegate
            {
                _textBox.Focus();
                _textBox.SelectAll();
            };
        }

        public static string Show(IWin32Window owner, string title, string prompt, string defaultValue)
        {
            using (InputDialog dlg = new InputDialog(title, prompt, defaultValue))
            {
                if (dlg.ShowDialog(owner) == DialogResult.OK && dlg.InputText.Length > 0)
                {
                    return dlg.InputText;
                }
                return null;
            }
        }
    }

    /// <summary>图标与界面小工具</summary>
    public static class UiUtil
    {
        public static Icon LoadAppIcon(string qianwenExePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(qianwenExePath) && File.Exists(qianwenExePath))
                {
                    return Icon.ExtractAssociatedIcon(qianwenExePath);
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        public static Color LightBlue { get { return Color.FromArgb(232, 240, 255); } }
        public static Color AltRow { get { return Color.FromArgb(248, 249, 251); } }
        public static Color Accent { get { return Color.FromArgb(64, 128, 220); } }
    }
}
