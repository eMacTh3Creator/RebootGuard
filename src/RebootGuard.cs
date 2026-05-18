// RebootGuard - blocks Windows shutdown/reboot/logoff until a password is entered.
// Target: .NET Framework 4.x WinForms. Build with build.ps1 (uses in-box csc.exe, no SDK).
//
// Behavior:
//   - Runs as a hidden interactive process with a system-tray icon.
//   - Intercepts WM_QUERYENDSESSION. When Windows tries to reboot/shutdown/log off,
//     it pops a password prompt. Correct password -> shutdown is allowed.
//     Wrong/cancel -> shutdown is blocked (best effort; see README for limits).
//   - Tray menu: Change password (requires current password), Exit (requires password).
//
// Password storage: %ProgramData%\RebootGuard\config.cfg
//   line 1 = base64(salt, 16 bytes)
//   line 2 = base64( SHA256(salt || UTF8(password)) )

using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace RebootGuard
{
    internal static class Native
    {
        public const int WM_QUERYENDSESSION = 0x0011;
        public const int WM_ENDSESSION      = 0x0016;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        public static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);
    }

    internal static class PasswordStore
    {
        private static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RebootGuard"); }
        }
        private static string CfgPath
        {
            get { return Path.Combine(Dir, "config.cfg"); }
        }

        public static bool IsConfigured
        {
            get { return File.Exists(CfgPath); }
        }

        public static void SetPassword(string password)
        {
            Directory.CreateDirectory(Dir);
            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            byte[] hash = Hash(salt, password);
            File.WriteAllLines(CfgPath, new[]
            {
                Convert.ToBase64String(salt),
                Convert.ToBase64String(hash)
            });
        }

        public static bool Verify(string password)
        {
            try
            {
                var lines = File.ReadAllLines(CfgPath);
                byte[] salt = Convert.FromBase64String(lines[0]);
                byte[] expected = Convert.FromBase64String(lines[1]);
                byte[] actual = Hash(salt, password);
                return FixedTimeEquals(expected, actual);
            }
            catch { return false; }
        }

        private static byte[] Hash(byte[] salt, string password)
        {
            byte[] pw = Encoding.UTF8.GetBytes(password ?? string.Empty);
            byte[] buf = new byte[salt.Length + pw.Length];
            Buffer.BlockCopy(salt, 0, buf, 0, salt.Length);
            Buffer.BlockCopy(pw, 0, buf, salt.Length, pw.Length);
            using (var sha = SHA256.Create()) return sha.ComputeHash(buf);
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    // Simple modal dialog: one or two password fields.
    internal sealed class PasswordDialog : Form
    {
        private readonly TextBox _p1 = new TextBox();
        private readonly TextBox _p2 = new TextBox();
        public string Value1 { get { return _p1.Text; } }
        public string Value2 { get { return _p2.Text; } }

        public PasswordDialog(string title, string prompt, bool confirmField)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(340, confirmField ? 170 : 130);

            var lbl = new Label { Text = prompt, AutoSize = false, Bounds = new Rectangle(12, 12, 316, 20) };
            _p1.UseSystemPasswordChar = true;
            _p1.Bounds = new Rectangle(12, 36, 316, 24);

            var lbl2 = new Label { Text = "Confirm password:", AutoSize = false, Bounds = new Rectangle(12, 68, 316, 20) };
            _p2.UseSystemPasswordChar = true;
            _p2.Bounds = new Rectangle(12, 92, 316, 24);

            int btnY = confirmField ? 128 : 84;
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(160, btnY, 80, 28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(248, btnY, 80, 28) };

            Controls.Add(lbl);
            Controls.Add(_p1);
            if (confirmField) { Controls.Add(lbl2); Controls.Add(_p2); }
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            Load += (s, e) => { Activate(); _p1.Focus(); };
        }
    }

    // Hidden main window that owns the shutdown-block behavior.
    internal sealed class GuardForm : Form
    {
        private readonly NotifyIcon _tray;
        private bool _allowExit;

        public GuardForm()
        {
            // Keep the window real (needs an HWND) but invisible.
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
            Opacity = 0;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Status", null, (s, e) =>
                MessageBox.Show("RebootGuard is active. Reboot/shutdown/logoff is blocked until the password is entered.",
                    "RebootGuard", MessageBoxButtons.OK, MessageBoxIcon.Information));
            menu.Items.Add("Change password", null, (s, e) => ChangePassword());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit (requires password)", null, (s, e) => RequestExit());

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Visible = true,
                Text = "RebootGuard - reboot blocked",
                ContextMenuStrip = menu
            };

            Native.SetProcessShutdownParameters(0x100, 0);
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_QUERYENDSESSION)
            {
                bool allow = PromptAndVerify();
                if (!allow)
                {
                    Native.ShutdownBlockReasonCreate(Handle,
                        "RebootGuard: password required to reboot or shut down this server.");
                    m.Result = IntPtr.Zero; // FALSE -> block
                }
                else
                {
                    Native.ShutdownBlockReasonDestroy(Handle);
                    _allowExit = true;
                    m.Result = (IntPtr)1;   // TRUE -> allow
                }
                return;
            }

            if (m.Msg == Native.WM_ENDSESSION)
            {
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);
        }

        private bool PromptAndVerify()
        {
            using (var dlg = new PasswordDialog(
                "RebootGuard - Authorization required",
                "A reboot/shutdown was requested. Enter the RebootGuard password to allow it:",
                false))
            {
                return dlg.ShowDialog(this) == DialogResult.OK && PasswordStore.Verify(dlg.Value1);
            }
        }

        private void ChangePassword()
        {
            if (PasswordStore.IsConfigured)
            {
                using (var cur = new PasswordDialog("RebootGuard", "Enter the CURRENT password:", false))
                {
                    if (cur.ShowDialog(this) != DialogResult.OK || !PasswordStore.Verify(cur.Value1))
                    {
                        MessageBox.Show("Current password is incorrect.", "RebootGuard",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
            }
            using (var dlg = new PasswordDialog("RebootGuard", "Enter the NEW password:", true))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (string.IsNullOrEmpty(dlg.Value1) || dlg.Value1 != dlg.Value2)
                {
                    MessageBox.Show("Passwords were empty or did not match. No change made.", "RebootGuard",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                PasswordStore.SetPassword(dlg.Value1);
                MessageBox.Show("Password updated.", "RebootGuard",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void RequestExit()
        {
            using (var dlg = new PasswordDialog("RebootGuard", "Enter the password to exit RebootGuard:", false))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && PasswordStore.Verify(dlg.Value1))
                {
                    _allowExit = true;
                    Native.ShutdownBlockReasonDestroy(Handle);
                    _tray.Visible = false;
                    Application.Exit();
                }
                else
                {
                    MessageBox.Show("Incorrect password. RebootGuard will keep running.", "RebootGuard",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Block taskkill-via-close / user close unless authorized or OS session ending after allow.
            if (!_allowExit && e.CloseReason != CloseReason.WindowsShutDown)
            {
                e.Cancel = true;
                return;
            }
            _tray.Visible = false;
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "Global\\RebootGuard_SingleInstance", out createdNew))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool setOnly = Array.Exists(args,
                    a => a.Equals("--set-password", StringComparison.OrdinalIgnoreCase));

                if (setOnly || !PasswordStore.IsConfigured)
                {
                    if (PasswordStore.IsConfigured)
                    {
                        using (var cur = new PasswordDialog("RebootGuard", "Enter the CURRENT password:", false))
                        {
                            if (cur.ShowDialog() != DialogResult.OK || !PasswordStore.Verify(cur.Value1))
                            {
                                MessageBox.Show("Current password incorrect. Aborting.", "RebootGuard",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                                return;
                            }
                        }
                    }
                    using (var dlg = new PasswordDialog("RebootGuard - Set password",
                        "Set the password that will be required to reboot/shut down:", true))
                    {
                        if (dlg.ShowDialog() != DialogResult.OK ||
                            string.IsNullOrEmpty(dlg.Value1) || dlg.Value1 != dlg.Value2)
                        {
                            MessageBox.Show("Password not set (empty or mismatch). Aborting.", "RebootGuard",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        PasswordStore.SetPassword(dlg.Value1);
                    }
                    if (setOnly)
                    {
                        MessageBox.Show("Password saved.", "RebootGuard",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }

                if (!createdNew)
                {
                    MessageBox.Show("RebootGuard is already running.", "RebootGuard",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.Run(new GuardForm());
            }
        }
    }
}
