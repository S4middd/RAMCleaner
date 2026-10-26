// RAM Cleaner - utilitas pembersih memori untuk Windows.
//
// Build: jalankan build.bat (csc.exe .NET Framework, tanpa SDK).
// Bahasa: C# 5 (csc v4.0.30319) - hindari string interpolation, ?., nameof.
//
// UI memakai WinForms (BUKAN WPF) dengan alasan eksplisit: runtime WPF
// memuat PresentationFramework + compositor yang bekerjaannya 40-60 MB.
// Untuk aplikasi pembersih RAM yang 99% waktunya tidur di tray, itu ironis.
// WinForms dengan gambar manual (GDI+) cukup dan footprint-nya kecil.
// Layout ringkas: kontrol utama selalu tampak, alat lanjutan (mode mendalam,
// restart explorer, daftar proses) disembunyikan di panel yang bisa dibuka.
//
// Catatan jujur soal cara kerjanya: EmptyWorkingSet dan purge standby list TIDAK
// menghapus data dari RAM. Keduanya memindahkan halaman keluar dari working set
// proses ke standby list / pagefile. Angka "Available RAM naik" karena itu mengukur
// berapa banyak halaman yang dipindahkan, bukan keuntungan bersih: aplikasi yang
// halamannya diambil akan memuat ulang dari disk dan sesaat terasa lebih lambat.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RamCleaner
{
    internal static class MemApi
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("ntdll.dll")]
        public static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        public const int SystemMemoryListInformation = 80;

        // MEMORY_LIST_COMMAND - perintah yang masih didukung Windows modern.
        // Ini set yang sama dipakai RAMMap.
        public const int CmdEmptyWorkingSets = 2;            // termasuk system working set (file cache)
        public const int CmdFlushModifiedList = 3;           // tulis halaman kotor ke disk -> jadi standby
        public const int CmdPurgeStandbyList = 4;
        public const int CmdPurgeLowPriorityStandbyList = 5;

        private static readonly int SizeMemStatus = Marshal.SizeOf(typeof(MEMORYSTATUSEX));

        public static bool TryGetStatus(out MEMORYSTATUSEX s)
        {
            s = new MEMORYSTATUSEX();
            s.dwLength = (uint)SizeMemStatus;
            return GlobalMemoryStatusEx(ref s);
        }

        // Mengembalikan null bila sukses, atau deskripsi error bila gagal.
        public static string RunCommand(int command)
        {
            try
            {
                int cmd = command;
                int st = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, 4);
                if (st == 0)
                {
                    return null;
                }
                if (st == unchecked((int)0xC0000061))
                {
                    return "hak istimewa tidak dimiliki (0xC0000061)";
                }
                if (st == unchecked((int)0xC0000002))
                {
                    return "tidak didukung Windows ini (0xC0000002)";
                }
                return "NTSTATUS 0x" + st.ToString("X8");
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    internal static class Privilege
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public LUID Luid;
            public int Attributes;
        }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        private const uint TOKEN_QUERY = 0x8;
        private const int SE_PRIVILEGE_ENABLED = 0x2;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string systemName, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle, bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, int bufferLength,
            IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static bool Enable(string privilegeName)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                // Pseudo-handle kernel32 (-1): tidak perlu ditutup, dan tidak
                // membocorkan Process object seperti Process.GetCurrentProcess().Handle.
                if (!OpenProcessToken(MemApi.GetCurrentProcess(),
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                {
                    return false;
                }

                LUID luid;
                if (!LookupPrivilegeValue(null, privilegeName, out luid))
                {
                    return false;
                }

                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Luid = luid;
                tp.Attributes = SE_PRIVILEGE_ENABLED;

                bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                // AdjustTokenPrivileges tetap TRUE walau privilege tidak dimiliki;
                // ERROR_NOT_ALL_ASSIGNED (1300) yang menandakan kegagalan sebenarnya.
                int err = Marshal.GetLastWin32Error();
                return ok && err == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero) { CloseHandle(token); }
            }
        }
    }

    // Item baris daftar proses. StartTicks dipakai untuk memastikan PID belum
    // didaur ulang oleh proses lain sebelum kita membersihkan / mengakhiri.
    public class ProcItem
    {
        public string Name;
        public int Id;
        public double MemMB;
        public long StartTicks;
    }

    internal static class Tray
    {
        private static NotifyIcon _ni;
        private static bool _hintShown;

        public static bool Active { get { return _ni != null; } }

        public static void Init()
        {
            try
            {
                _ni = new NotifyIcon();
                _ni.Icon = LoadIcon();
                _ni.Text = "RAM Cleaner";
                _ni.Visible = true;

                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("Tampilkan jendela", null, delegate { Program.ShowFromTray(); });
                menu.Items.Add("Bersihkan RAM sekarang", null, delegate { Program.CleanFromTray(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Keluar", null, delegate { Program.ExitApp(); });
                _ni.ContextMenuStrip = menu;

                _ni.DoubleClick += delegate { Program.ShowFromTray(); };
            }
            catch
            {
                _ni = null;
            }
        }

        private static Icon LoadIcon()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    Icon ic = Icon.ExtractAssociatedIcon(exe);
                    if (ic != null) { return ic; }
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        public static void SetTooltip(string text)
        {
            if (_ni == null) { return; }
            try
            {
                // NotifyIcon.Text dibatasi 63 karakter.
                if (text != null && text.Length > 63) { text = text.Substring(0, 63); }
                _ni.Text = text;
            }
            catch { }
        }

        public static void Balloon(string title, string body)
        {
            if (_ni == null) { return; }
            try
            {
                _ni.ShowBalloonTip(4000, title, body, ToolTipIcon.Info);
            }
            catch { }
        }

        public static void MinimizeHint()
        {
            if (_hintShown) { return; }
            _hintShown = true;
            Balloon("RAM Cleaner", "Masih berjalan di tray. Klik dua kali ikonnya untuk membuka lagi.");
        }

        public static void Dispose()
        {
            if (_ni == null) { return; }
            try
            {
                _ni.Visible = false;
                _ni.Dispose();
            }
            catch { }
            _ni = null;
        }
    }

    // Panel dengan double buffering: tanpa ini repaint tiap tick kelihatan
    // kedip di GDI+.
    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
        }
    }

    internal static class Program
    {
        // Palet Catppuccin Mocha.
        private static readonly Color ColBg = Color.FromArgb(0x1E, 0x1E, 0x2E);
        private static readonly Color ColSurface = Color.FromArgb(0x31, 0x32, 0x44);
        private static readonly Color ColSurfaceHi = Color.FromArgb(0x45, 0x47, 0x5A);
        private static readonly Color ColText = Color.FromArgb(0xCD, 0xD6, 0xF4);
        private static readonly Color ColSubtext = Color.FromArgb(0xA6, 0xAD, 0xC8);
        private static readonly Color ColBlue = Color.FromArgb(0x89, 0xB4, 0xFA);
        private static readonly Color ColGreen = Color.FromArgb(0xA6, 0xE3, 0xA1);
        private static readonly Color ColYellow = Color.FromArgb(0xF9, 0xE2, 0xAF);
        private static readonly Color ColRed = Color.FromArgb(0xF3, 0x8B, 0xA8);
        private static readonly Color ColTeal = Color.FromArgb(0x94, 0xE2, 0xD5);

        // GDI+ object di-cache sekali, tidak dibuat di setiap Paint.
        private static readonly SolidBrush BrSurface = new SolidBrush(ColSurface);
        private static readonly SolidBrush BrGreen = new SolidBrush(ColGreen);
        private static readonly SolidBrush BrYellow = new SolidBrush(ColYellow);
        private static readonly SolidBrush BrRed = new SolidBrush(ColRed);

        private static Form _form;
        private static Mutex _singleInstance;

        private static Label _lblUsage;
        private static BufferedPanel _meterPanel;
        private static CheckBox _chkAuto;
        private static TextBox _txtThreshold;
        private static Button _btnClean;
        private static Label _lblStatus;
        private static Button _btnAdv;
        private static Panel _advPanel;
        private static CheckBox _chkAggressive;
        private static Button _btnRestartExplorer;
        private static ListView _lvProcs;
        private static ContextMenuStrip _procMenu;
        private static ToolStripMenuItem _miClean;
        private static ToolStripMenuItem _miKill;
        private static System.Windows.Forms.Timer _timer;

        private static int _lastPct;
        private static volatile bool _isCleaning;
        private static bool _menuOpen;
        private static int _lastCleanTicks;
        private static int _tickCounter;

        // PID -> baris ListView, supaya update in-place (seleksi & scroll tidak hilang).
        private static readonly Dictionary<int, ListViewItem> _rows =
            new Dictionary<int, ListViewItem>();

        private static int _ownPid;
        private static int _ownSessionId;

        private static readonly string[] Excluded = new string[]
        {
            "system", "idle", "registry", "memory compression", "secure system",
            "memcompression", "smss", "csrss", "wininit", "winlogon",
            "services", "lsass", "svchost", "dwm", "fontdrvhost",
            "sihost", "explorer", "conhost", "wudfhost"
        };

        // Mode mendalam: hanya proses yang benar-benar kritis dilewati.
        // svchost, dwm, explorer, dll. ikut dibersihkan seperti RAMMap.
        private static readonly string[] ExcludedCritical = new string[]
        {
            "system", "idle", "registry", "memory compression", "secure system",
            "memcompression", "smss", "csrss", "wininit", "winlogon",
            "services", "lsass"
        };

        private const int FormWidth = 340;
        private const int FormHeightCompact = 272;
        private const int FormHeightFull = 580;

        [STAThread]
        public static void Main()
        {
            try
            {
                bool created;
                _singleInstance = new Mutex(true, "Local\\RAMCleaner_SingleInstance", out created);
                if (!created)
                {
                    MessageBox.Show(
                        "RAM Cleaner sudah berjalan.\nCek ikonnya di system tray.",
                        "RAM Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            catch
            {
                // Mutex gagal dibuat bukan alasan untuk membatalkan start.
                _singleInstance = null;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
            }
            catch { }

            try
            {
                RunApp();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "RAM Cleaner - Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Tray.Dispose();
                ReleaseInstanceLock();
            }
        }

        private static void ReleaseInstanceLock()
        {
            if (_singleInstance == null) { return; }
            try { _singleInstance.ReleaseMutex(); } catch { }
            try { _singleInstance.Dispose(); } catch { }
            _singleInstance = null;
        }

        private static void RunApp()
        {
            CacheOwnIdentity();
            _lastCleanTicks = Environment.TickCount - 600000;

            _form = BuildForm();

            _form.Shown += delegate
            {
                // Judul jendela gelap: DWMWA_USE_IMMERSIVE_DARK_MODE,
                // 20 di Windows 10 2004+, 19 sebelumnya.
                try
                {
                    int on = 1;
                    if (MemApi.DwmSetWindowAttribute(_form.Handle, 20, ref on, 4) != 0)
                    {
                        MemApi.DwmSetWindowAttribute(_form.Handle, 19, ref on, 4);
                    }
                }
                catch { }

                UpdateDisplay(true);
                // Buang halaman startup yang tidak diperlukan lagi.
                TrimOwnWorkingSet();
            };

            _form.Resize += delegate
            {
                if (_form.WindowState == FormWindowState.Minimized && Tray.Active)
                {
                    _form.Hide();
                    SetTimerSlow(true);
                    TrimOwnWorkingSet();
                    Tray.MinimizeHint();
                }
            };

            _form.FormClosed += delegate
            {
                if (_timer != null) { _timer.Stop(); }
                Tray.Dispose();
                ReleaseInstanceLock();
            };

            Tray.Init();

            // Manifest sudah requireAdministrator, jadi normalnya cabang ini tidak
            // terpakai. Tetap disimpan untuk build tanpa manifest.
            if (!IsAdmin())
            {
                _lblStatus.Text = "Berjalan tanpa hak Admin - pembersihan tidak akan menyeluruh.";
            }

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 2000;
            _timer.Tick += delegate { OnTick(); };
            _timer.Start();

            Application.Run(_form);
        }

        private static void CacheOwnIdentity()
        {
            try
            {
                using (Process me = Process.GetCurrentProcess())
                {
                    _ownPid = me.Id;
                    _ownSessionId = me.SessionId;
                }
            }
            catch
            {
                _ownPid = 0;
                _ownSessionId = -1;
            }
        }

        private static bool IsAdmin()
        {
            try
            {
                return new WindowsPrincipal(WindowsIdentity.GetCurrent())
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // Saat jendela tersembunyi di tray, monitoring dilambatkan: tidak ada
        // yang melihat angka, dan auto-clean tetap jalan walau 5 detik.
        private static void SetTimerSlow(bool slow)
        {
            if (_timer == null) { return; }
            _timer.Interval = slow ? 5000 : 2000;
        }

        private static void TrimOwnWorkingSet()
        {
            // GC dulu supaya halaman heap yang bebas ikut terlepas, baru
            // EmptyWorkingSet mendorong sisanya ke standby list.
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch { }
            try { MemApi.EmptyWorkingSet(MemApi.GetCurrentProcess()); } catch { }
        }

        internal static void ExitApp()
        {
            if (_form != null && !_form.IsDisposed)
            {
                _form.Close();
            }
        }

        internal static void ShowFromTray()
        {
            if (_form == null || _form.IsDisposed) { return; }
            _form.BeginInvoke(new Action(delegate
            {
                _form.Show();
                _form.WindowState = FormWindowState.Normal;
                SetTimerSlow(false);
                _form.Activate();
            }));
        }

        internal static void CleanFromTray()
        {
            if (_form == null || _form.IsDisposed) { return; }
            _form.BeginInvoke(new Action(delegate { CleanRam(false); }));
        }

        // ===================== UI =====================

        private static Button MakeButton(string text, Color back, Color hover, Color fore, int height)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.BackColor = back;
            b.ForeColor = fore;
            b.Height = height;
            b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
            return b;
        }

        private static Form BuildForm()
        {
            Form f = new Form();
            f.Text = "RAM Cleaner";
            f.ClientSize = new Size(FormWidth, FormHeightCompact);
            f.FormBorderStyle = FormBorderStyle.FixedSingle;
            f.MaximizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.BackColor = ColBg;
            f.ForeColor = ColText;
            f.Font = new Font("Segoe UI", 9f);

            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    f.Icon = Icon.ExtractAssociatedIcon(exe);
                }
            }
            catch { }

            // ---------- Bagian utama (selalu tampak) ----------
            _lblUsage = new Label();
            _lblUsage.Text = "-";
            _lblUsage.ForeColor = ColGreen;
            _lblUsage.BackColor = Color.Transparent;
            _lblUsage.AutoSize = false;
            _lblUsage.TextAlign = ContentAlignment.MiddleCenter;
            _lblUsage.Font = new Font("Segoe UI", 38f, FontStyle.Bold);
            _lblUsage.SetBounds(10, 6, 320, 64);
            f.Controls.Add(_lblUsage);

            _meterPanel = new BufferedPanel();
            _meterPanel.SetBounds(10, 74, 320, 12);
            _meterPanel.Paint += PaintMeter;
            f.Controls.Add(_meterPanel);

            _chkAuto = new CheckBox();
            _chkAuto.Text = "Auto bersihkan di atas";
            _chkAuto.ForeColor = ColSubtext;
            _chkAuto.BackColor = Color.Transparent;
            _chkAuto.AutoSize = true;
            _chkAuto.SetBounds(10, 94, 160, 22);
            _chkAuto.CheckedChanged += delegate { ValidateThreshold(true); };
            f.Controls.Add(_chkAuto);

            _txtThreshold = new TextBox();
            _txtThreshold.Text = "85";
            _txtThreshold.BorderStyle = BorderStyle.FixedSingle;
            _txtThreshold.BackColor = ColSurface;
            _txtThreshold.ForeColor = ColText;
            _txtThreshold.TextAlign = HorizontalAlignment.Center;
            _txtThreshold.SetBounds(240, 92, 42, 23);
            _txtThreshold.TextChanged += delegate { ValidateThreshold(false); };
            f.Controls.Add(_txtThreshold);

            Label lblPct = new Label();
            lblPct.Text = "%";
            lblPct.ForeColor = ColSubtext;
            lblPct.BackColor = Color.Transparent;
            lblPct.AutoSize = true;
            lblPct.SetBounds(286, 95, 20, 18);
            f.Controls.Add(lblPct);

            _btnClean = MakeButton("Bersihkan RAM", ColBlue, ColSurfaceHi, ColBg, 42);
            _btnClean.SetBounds(10, 124, 320, 42);
            _btnClean.Click += delegate { CleanRam(false); };
            f.Controls.Add(_btnClean);

            _lblStatus = new Label();
            _lblStatus.Text = "";
            _lblStatus.ForeColor = ColTeal;
            _lblStatus.BackColor = Color.Transparent;
            _lblStatus.AutoSize = false;
            _lblStatus.TextAlign = ContentAlignment.TopLeft;
            _lblStatus.SetBounds(10, 172, 320, 56);
            f.Controls.Add(_lblStatus);

            _btnAdv = MakeButton("Tampilkan alat lanjutan \u25BE", ColSurface, ColSurfaceHi, ColText, 24);
            _btnAdv.SetBounds(10, 232, 320, 24);
            _btnAdv.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            _btnAdv.Click += delegate { ToggleAdvanced(); };
            f.Controls.Add(_btnAdv);

            // ---------- Panel alat lanjutan (sembunyi by default) ----------
            _advPanel = new Panel();
            _advPanel.Location = new Point(0, 264);
            _advPanel.Size = new Size(FormWidth, FormHeightFull - FormHeightCompact);
            _advPanel.BackColor = ColBg;
            _advPanel.Visible = false;
            f.Controls.Add(_advPanel);

            _chkAggressive = new CheckBox();
            _chkAggressive.Text = "Mode mendalam (proses sistem + file cache)";
            _chkAggressive.ForeColor = ColSubtext;
            _chkAggressive.BackColor = Color.Transparent;
            _chkAggressive.AutoSize = true;
            _chkAggressive.SetBounds(10, 6, 320, 22);
            // Sengaja OFF secara default: mode ini menyapu dwm, svchost dan explorer,
            // yang bikin desktop tersendat dan biasanya meninggalkan mesin lebih
            // lambat. Harus jadi pilihan sadar, bukan default.
            _chkAggressive.Checked = false;
            _advPanel.Controls.Add(_chkAggressive);

            ToolTip tip = new ToolTip();
            tip.SetToolTip(_chkAggressive,
                "Menyapu working set semua proses kecuali yang kritis, plus system "
                + "working set (file cache), dan mem-flush modified page list. "
                + "Efeknya lebih besar tapi desktop bisa tersendat sesaat.");

            _btnRestartExplorer = MakeButton("Restart Explorer", ColYellow, ColSurfaceHi, ColBg, 36);
            _btnRestartExplorer.SetBounds(10, 32, 320, 36);
            _btnRestartExplorer.Click += delegate { RestartExplorer(); };
            _advPanel.Controls.Add(_btnRestartExplorer);

            Label lblList = new Label();
            lblList.Text = "PROSES TERATAS BERDASARKAN RAM (klik kanan untuk aksi)";
            lblList.ForeColor = ColSubtext;
            lblList.BackColor = Color.Transparent;
            lblList.AutoSize = false;
            lblList.TextAlign = ContentAlignment.MiddleLeft;
            lblList.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            lblList.SetBounds(10, 76, 320, 16);
            _advPanel.Controls.Add(lblList);

            _lvProcs = new ListView();
            _lvProcs.View = View.Details;
            _lvProcs.FullRowSelect = true;
            _lvProcs.MultiSelect = false;
            _lvProcs.HideSelection = true;
            _lvProcs.BorderStyle = BorderStyle.FixedSingle;
            _lvProcs.BackColor = ColBg;
            _lvProcs.ForeColor = ColText;
            _lvProcs.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _lvProcs.SetBounds(10, 94, 320, 212);

            _lvProcs.Columns.Add("Nama Proses", 180);
            _lvProcs.Columns.Add("PID", 50);
            _lvProcs.Columns.Add("RAM (MB)", 72);

            _procMenu = new ContextMenuStrip();
            _miClean = new ToolStripMenuItem("Bersihkan proses ini");
            _miKill = new ToolStripMenuItem("Akhiri proses ini");
            _miClean.Click += delegate { CleanSingle(SelectedProcess()); };
            _miKill.Click += delegate { KillSingle(SelectedProcess()); };
            _procMenu.Items.Add(_miClean);
            _procMenu.Items.Add(_miKill);
            _procMenu.Opened += delegate
            {
                // Selama menu terbuka, daftar tidak di-refresh supaya baris tidak
                // bergeser / terganti di bawah kursor.
                _menuOpen = true;
                ProcItem sel = SelectedProcess();
                bool usable = sel != null && !_isCleaning;
                _miClean.Enabled = usable;
                _miKill.Enabled = usable;
                _miClean.Text = sel != null ? "Bersihkan '" + sel.Name + "'" : "Bersihkan proses ini";
                _miKill.Text = sel != null ? "Akhiri '" + sel.Name + "'" : "Akhiri proses ini";
            };
            _procMenu.Closed += delegate { _menuOpen = false; };
            _lvProcs.ContextMenuStrip = _procMenu;

            _advPanel.Controls.Add(_lvProcs);

            return f;
        }

        private static void ToggleAdvanced()
        {
            bool show = !_advPanel.Visible;
            _advPanel.Visible = show;
            _form.ClientSize = new Size(FormWidth, show ? FormHeightFull : FormHeightCompact);
            _btnAdv.Text = show ? "Sembunyikan alat lanjutan \u25BE" : "Tampilkan alat lanjutan \u25BE";
        }

        private static ProcItem SelectedProcess()
        {
            if (_lvProcs == null || _lvProcs.SelectedItems.Count == 0) { return null; }
            return _lvProcs.SelectedItems[0].Tag as ProcItem;
        }

        // ===================== Render meter =====================

        private static SolidBrush MeterBrush()
        {
            if (_lastPct < 60) { return BrGreen; }
            if (_lastPct < 85) { return BrYellow; }
            return BrRed;
        }

        private static void PaintMeter(object sender, PaintEventArgs e)
        {
            Rectangle r = _meterPanel.ClientRectangle;
            if (r.Width <= 0 || r.Height <= 0) { return; }
            e.Graphics.FillRectangle(BrSurface, r);
            int w = (int)(r.Width * Math.Max(0, Math.Min(100, _lastPct)) / 100.0);
            if (w > 0)
            {
                e.Graphics.FillRectangle(MeterBrush(), 0, 0, w, r.Height);
            }
        }

        // ===================== Tick & display =====================

        private static void OnTick()
        {
            _tickCounter++;

            // Snapshot proses cukup tiap 3 tick (6 detik); saat jendela
            // tersembunyi di tray tidak ada yang melihat daftarnya.
            bool snap = _form.Visible && _tickCounter % 3 == 0;
            UpdateDisplay(snap);

            // Saat di tray, trim ulang tiap ~1 menit supaya footprint tetap
            // rendah walau timer dan tooltip sesekali memfault halaman balik.
            if (!_form.Visible && _tickCounter % 12 == 0)
            {
                TrimOwnWorkingSet();
            }

            CheckAutoClean();
        }

        private static void UpdateDisplay(bool snapshotProcesses)
        {
            try
            {
                MemApi.MEMORYSTATUSEX s;
                if (!MemApi.TryGetStatus(out s))
                {
                    // Jangan tampilkan 0% seolah-olah itu data valid. _lastPct
                    // dibuat negatif supaya CheckAutoClean tidak memakai angka
                    // basi dari tick sebelumnya.
                    _lastPct = -1;
                    _lblUsage.Text = "?";
                    _lblUsage.ForeColor = ColRed;
                    Tray.SetTooltip("RAM Cleaner - status memori tidak terbaca");
                    return;
                }

                int pct = (int)s.dwMemoryLoad;
                _lastPct = pct;

                _lblUsage.Text = pct + "%";
                if (pct < 60) { _lblUsage.ForeColor = ColGreen; }
                else if (pct < 85) { _lblUsage.ForeColor = ColYellow; }
                else { _lblUsage.ForeColor = ColRed; }
                _meterPanel.Invalidate();

                Tray.SetTooltip("RAM Cleaner - " + pct + "% terpakai");

                // Enumerasi seluruh proses itu mahal; hanya saat diminta, tidak
                // sedang membersihkan, dan menu konteks tidak terbuka.
                if (snapshotProcesses && !_isCleaning && !_menuOpen)
                {
                    MergeProcesses(SnapshotProcesses());
                }
            }
            catch { }
        }

        // ===================== Daftar proses =====================

        private static List<ProcItem> SnapshotProcesses()
        {
            List<ProcItem> list = new List<ProcItem>();
            Process[] procs = Process.GetProcesses();
            foreach (Process p in procs)
            {
                try
                {
                    long ws = p.WorkingSet64;
                    if (ws <= 10485760L) { continue; }

                    ProcItem it = new ProcItem();
                    it.Name = p.ProcessName;
                    it.Id = p.Id;
                    it.MemMB = Math.Round(ws / 1048576.0, 1);
                    it.StartTicks = SafeStartTicks(p);
                    list.Add(it);
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            list.Sort(delegate(ProcItem a, ProcItem b) { return b.MemMB.CompareTo(a.MemMB); });
            if (list.Count > 25)
            {
                list.RemoveRange(25, list.Count - 25);
            }
            return list;
        }

        private static long SafeStartTicks(Process p)
        {
            // StartTime melempar Access Denied untuk sebagian proses sistem.
            try { return p.StartTime.Ticks; }
            catch { return 0; }
        }

        // Update di tempat supaya seleksi dan posisi scroll tidak hilang setiap
        // tick (dulu seluruh isi ListView diganti dengan List baru).
        private static void MergeProcesses(List<ProcItem> snap)
        {
            Dictionary<int, ProcItem> byId = new Dictionary<int, ProcItem>(snap.Count);
            for (int i = 0; i < snap.Count; i++)
            {
                byId[snap[i].Id] = snap[i];
            }

            // Hapus baris proses yang sudah tidak ada.
            for (int i = _lvProcs.Items.Count - 1; i >= 0; i--)
            {
                ProcItem old = _lvProcs.Items[i].Tag as ProcItem;
                if (old == null || !byId.ContainsKey(old.Id))
                {
                    _rows.Remove(old != null ? old.Id : -1);
                    _lvProcs.Items.RemoveAt(i);
                }
            }

            // Ingat seleksi: reorder di bawah bisa menggeser fokus.
            int selectedPid = -1;
            ProcItem sel = SelectedProcess();
            if (sel != null) { selectedPid = sel.Id; }

            // Samakan urutan dengan snapshot. Invarian: setelah indeks i
            // diproses, posisi 0..i-1 persis sama dengan snapshot 0..i-1.
            _lvProcs.BeginUpdate();
            try
            {
                for (int i = 0; i < snap.Count; i++)
                {
                    ProcItem fresh = snap[i];
                    ListViewItem row;
                    if (!_rows.TryGetValue(fresh.Id, out row))
                    {
                        row = new ListViewItem(fresh.Name);
                        row.SubItems.Add(fresh.Id.ToString(CultureInfo.InvariantCulture));
                        row.SubItems.Add(fresh.MemMB.ToString("N1", CultureInfo.InvariantCulture));
                        row.Tag = fresh;
                        _rows[fresh.Id] = row;
                        _lvProcs.Items.Insert(Math.Min(i, _lvProcs.Items.Count), row);
                        continue;
                    }

                    ProcItem existing = row.Tag as ProcItem;
                    if (existing != null)
                    {
                        existing.Name = fresh.Name;
                        existing.MemMB = fresh.MemMB;
                        existing.StartTicks = fresh.StartTicks;
                    }

                    row.Text = fresh.Name;
                    row.SubItems[1].Text = fresh.Id.ToString(CultureInfo.InvariantCulture);
                    row.SubItems[2].Text = fresh.MemMB.ToString("N1", CultureInfo.InvariantCulture);

                    if (row.Index != i)
                    {
                        _lvProcs.Items.Remove(row);
                        _lvProcs.Items.Insert(Math.Min(i, _lvProcs.Items.Count), row);
                    }
                }
            }
            finally
            {
                _lvProcs.EndUpdate();
            }

            if (selectedPid >= 0 && _rows.ContainsKey(selectedPid))
            {
                _rows[selectedPid].Selected = true;
            }
        }

        // Memastikan PID masih milik proses yang sama sebelum kita bertindak.
        // Tanpa ini, PID yang sudah didaur ulang Windows bisa membuat kita
        // membersihkan - atau mengakhiri - proses yang sama sekali berbeda.
        private static Process ResolveVerified(ProcItem it, out string error)
        {
            error = null;
            Process p = null;
            try
            {
                p = Process.GetProcessById(it.Id);
            }
            catch
            {
                error = "Proses sudah tidak berjalan.";
                return null;
            }

            try
            {
                if (!string.Equals(p.ProcessName, it.Name, StringComparison.OrdinalIgnoreCase))
                {
                    error = "PID " + it.Id + " sekarang dipakai proses lain ('"
                        + p.ProcessName + "'). Dibatalkan.";
                    p.Dispose();
                    return null;
                }

                long ticks = SafeStartTicks(p);
                if (it.StartTicks != 0 && ticks != 0 && ticks != it.StartTicks)
                {
                    error = "PID " + it.Id + " sudah didaur ulang oleh proses baru. Dibatalkan.";
                    p.Dispose();
                    return null;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { p.Dispose(); } catch { }
                return null;
            }

            return p;
        }

        // ===================== Pembersihan =====================

        private sealed class CleanOutcome
        {
            public bool Deep;
            public bool FromAuto;
            public int Rounds;
            public int DistinctProcesses;
            public bool PrivProfile;
            public bool PrivQuota;
            public bool StatusOk;
            public int PctBefore;
            public int PctAfter;
            public double FreedMb;
            public readonly Dictionary<string, string> Failures =
                new Dictionary<string, string>();
        }

        private static void Note(CleanOutcome o, string stage, string err)
        {
            if (err == null) { return; }
            // Kegagalan diakumulasi per tahap, bukan ditimpa tiap putaran.
            o.Failures[stage] = err;
        }

        private static void CleanRam(bool fromAuto)
        {
            if (_isCleaning) { return; }

            int threshold;
            if (fromAuto && !TryThreshold(out threshold)) { return; }

            _isCleaning = true;
            SetBusy(true);

            bool deep = _chkAggressive.Checked;

            // Cast eksplisit: anonymous method tanpa parameter bisa dikonversi ke
            // ThreadStart maupun ParameterizedThreadStart (CS0121 di csc lama).
            Thread worker = new Thread((ThreadStart)delegate { CleanWorker(deep, fromAuto); });
            worker.IsBackground = true;
            worker.Name = "RamCleanWorker";
            worker.Start();
        }

        // Berjalan di thread background. Seluruh P/Invoke di sini tidak butuh UI
        // thread, jadi jendela tetap responsif.
        private static void CleanWorker(bool deep, bool fromAuto)
        {
            CleanOutcome o = new CleanOutcome();
            o.Deep = deep;
            o.FromAuto = fromAuto;

            try
            {
                Status("Menyiapkan hak istimewa sistem...");

                // Dua panggilan terpisah: dengan && yang kedua tidak pernah dicoba
                // kalau yang pertama gagal, padahal keduanya independen.
                o.PrivProfile = Privilege.Enable("SeProfileSingleProcessPrivilege");
                o.PrivQuota = Privilege.Enable("SeIncreaseQuotaPrivilege");
                if (!o.PrivProfile)
                {
                    Note(o, "SeProfileSingleProcessPrivilege", "tidak bisa diaktifkan");
                }
                if (!o.PrivQuota)
                {
                    Note(o, "SeIncreaseQuotaPrivilege", "tidak bisa diaktifkan");
                }

                MemApi.MEMORYSTATUSEX before;
                o.StatusOk = MemApi.TryGetStatus(out before);
                o.PctBefore = (int)before.dwMemoryLoad;

                string[] excl = deep ? ExcludedCritical : Excluded;
                int maxRounds = deep ? 6 : 2;
                HashSet<int> touched = new HashSet<int>();
                double prevAvailMb = -1;

                for (int r = 1; r <= maxRounds; r++)
                {
                    Status(string.Format(
                        deep
                            ? "Putaran {0}/{1}: membersihkan working set semua proses..."
                            : "Membersihkan ({0}/{1})...",
                        r, maxRounds));

                    SweepProcesses(excl, touched);
                    Thread.Sleep(deep ? 300 : 80);

                    if (deep)
                    {
                        // Mengosongkan system working set, termasuk file cache.
                        // Menggantikan SetSystemFileCacheSize yang lama: cara itu
                        // bisa meninggalkan batas cache terkunci di 0 sampai reboot
                        // bila proses restore gagal.
                        Note(o, "Empty working set sistem",
                            MemApi.RunCommand(MemApi.CmdEmptyWorkingSets));
                        Thread.Sleep(150);

                        // Halaman kotor ditulis ke disk lalu jadi standby, supaya
                        // purge standby di bawah benar-benar bisa melepasnya.
                        Note(o, "Flush modified page list",
                            MemApi.RunCommand(MemApi.CmdFlushModifiedList));
                        Thread.Sleep(150);
                    }

                    Note(o, "Purge standby prioritas rendah",
                        MemApi.RunCommand(MemApi.CmdPurgeLowPriorityStandbyList));
                    Thread.Sleep(120);
                    Note(o, "Purge standby list",
                        MemApi.RunCommand(MemApi.CmdPurgeStandbyList));
                    Thread.Sleep(deep ? 350 : 200);

                    o.Rounds = r;

                    MemApi.MEMORYSTATUSEX probe;
                    if (!MemApi.TryGetStatus(out probe)) { break; }

                    double availMb = probe.ullAvailPhys / 1048576.0;
                    if (prevAvailMb >= 0 && (availMb - prevAvailMb) < 30)
                    {
                        break;   // hasil sudah konvergen
                    }
                    prevAvailMb = availMb;
                }

                Status("Menutup: membersihkan working set aplikasi ini...");
                try { MemApi.EmptyWorkingSet(MemApi.GetCurrentProcess()); } catch { }

                Thread.Sleep(500);

                MemApi.MEMORYSTATUSEX after;
                if (MemApi.TryGetStatus(out after))
                {
                    o.PctAfter = (int)after.dwMemoryLoad;
                    long usedBefore = (long)(before.ullTotalPhys - before.ullAvailPhys);
                    long usedAfter = (long)(after.ullTotalPhys - after.ullAvailPhys);
                    double freed = (usedBefore - usedAfter) / 1048576.0;
                    o.FreedMb = freed < 0 ? 0 : freed;
                }
                else
                {
                    o.StatusOk = false;
                }

                o.DistinctProcesses = touched.Count;
            }
            catch (Exception ex)
            {
                Note(o, "Kesalahan tak terduga", ex.Message);
            }

            CleanOutcome captured = o;
            _form.BeginInvoke(new Action(delegate { FinishClean(captured); }));
        }

        private static void FinishClean(CleanOutcome o)
        {
            try
            {
                string modeText = o.Deep
                    ? "mode mendalam, " + o.Rounds + " putaran adaptif"
                    : "mode cepat, " + o.Rounds + " putaran";

                StringBuilder sb = new StringBuilder();
                if (o.FromAuto) { sb.Append("[AUTO] "); }

                // "DistinctProcesses" adalah jumlah PID unik, bukan akumulasi
                // per putaran.
                sb.Append(string.Format(
                    "Selesai. {0} proses diproses ({1}).",
                    o.DistinctProcesses, modeText));

                if (o.StatusOk)
                {
                    sb.Append(string.Format(
                        "\nRAM tersedia naik sekitar {0:N0} MB ({1}% → {2}%).",
                        o.FreedMb, o.PctBefore, o.PctAfter));
                }
                else
                {
                    sb.Append("\nStatus memori tidak bisa dibaca, selisih tidak dihitung.");
                }

                if (o.Failures.Count > 0)
                {
                    List<string> parts = new List<string>();
                    foreach (KeyValuePair<string, string> kv in o.Failures)
                    {
                        parts.Add(kv.Key + " (" + kv.Value + ")");
                    }
                    sb.Append("\nTahap gagal: " + string.Join("; ", parts.ToArray()));
                }

                _lblStatus.Text = sb.ToString();

                if (o.FromAuto && Tray.Active && !_form.Visible)
                {
                    Tray.Balloon("RAM Cleaner - auto bersih",
                        string.Format("{0}% → {1}%, sekitar {2:N0} MB dilepas.",
                            o.PctBefore, o.PctAfter, o.FreedMb));
                }

                UpdateDisplay(true);
            }
            finally
            {
                _lastCleanTicks = Environment.TickCount;
                _isCleaning = false;
                SetBusy(false);
            }
        }

        private static void SweepProcesses(string[] excluded, HashSet<int> touched)
        {
            Process[] procs = Process.GetProcesses();
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id == _ownPid) { continue; }
                    // ToLowerInvariant: ToLower() bergantung culture, dan di tr-TR
                    // "WININIT" jadi "wınınıt" sehingga daftar pengecualian bocor.
                    if (Array.IndexOf(excluded, p.ProcessName.ToLowerInvariant()) >= 0)
                    {
                        continue;
                    }
                    // Process.Handle melempar bila akses ditolak - itu jalur
                    // normal untuk proses terlindungi, bukan error yang perlu dicatat.
                    if (MemApi.EmptyWorkingSet(p.Handle))
                    {
                        touched.Add(p.Id);
                    }
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        // ===================== Auto clean =====================

        private static bool TryThreshold(out int value)
        {
            value = 0;
            if (_txtThreshold == null) { return false; }
            string raw = _txtThreshold.Text.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
            return value >= 50 && value <= 99;
        }

        private static void ValidateThreshold(bool announce)
        {
            int t;
            bool ok = TryThreshold(out t);
            _txtThreshold.BackColor = ok ? ColSurface : ColRed;
            _txtThreshold.ForeColor = ok ? ColText : ColBg;

            // Dulu ambang tidak valid membuat auto-clean mati tanpa tanda apa pun.
            if (!ok && (announce || _chkAuto.Checked))
            {
                _lblStatus.Text = "Ambang auto-clean tidak valid (harus 50-99). Auto-clean nonaktif.";
            }
        }

        private static void CheckAutoClean()
        {
            if (_isCleaning || !_chkAuto.Checked) { return; }

            int t;
            if (!TryThreshold(out t)) { return; }
            if (_lastPct < t) { return; }

            // Environment.TickCount monotonik. DateTime.Now bisa melompat mundur
            // (DST, koreksi NTP, user ubah jam) dan mematikan auto-clean sampai
            // sepanjang lompatan itu.
            int elapsed = unchecked(Environment.TickCount - _lastCleanTicks);
            if (elapsed < 60000) { return; }

            CleanRam(true);
        }

        // ===================== Aksi per proses =====================

        private static void CleanSingle(ProcItem it)
        {
            if (it == null) { return; }
            // Guard: tanpa ini aksi ini bisa berjalan bersamaan dengan CleanRam
            // dan saling menimpa status.
            if (_isCleaning) { return; }

            string err;
            Process p = ResolveVerified(it, out err);
            if (p == null)
            {
                _lblStatus.Text = err;
                return;
            }

            long beforeWs = 0;
            bool ok = false;
            int wsError = 0;
            try
            {
                beforeWs = p.WorkingSet64;
                ok = MemApi.EmptyWorkingSet(p.Handle);
                if (!ok)
                {
                    // Ditangkap di sini: p.Dispose() di finally bisa menimpa
                    // last-error thread sebelum pesan disusun.
                    wsError = Marshal.GetLastWin32Error();
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Gagal membersihkan '" + it.Name + "': " + ex.Message;
                return;
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }

            if (!ok)
            {
                _lblStatus.Text = "Gagal membersihkan '" + it.Name + "' (error "
                    + wsError + ").";
                return;
            }

            double freed = 0;
            try
            {
                using (Process again = Process.GetProcessById(it.Id))
                {
                    freed = Math.Max(0, (beforeWs - again.WorkingSet64) / 1048576.0);
                }
            }
            catch { }

            UpdateDisplay(true);
            string msg = string.Format(
                "Proses '{0}' dibersihkan (sekitar {1:N0} MB dilepas).", it.Name, freed);
            _lblStatus.Text = msg;
        }

        private static void KillSingle(ProcItem it)
        {
            if (it == null) { return; }
            if (_isCleaning) { return; }

            // Mengakhiri proses ini akan membuat Windows bluescreen atau logout paksa.
            if (Array.IndexOf(ExcludedCritical, it.Name.ToLowerInvariant()) >= 0)
            {
                _lblStatus.Text = "'" + it.Name + "' adalah proses kritis Windows - "
                    + "mengakhirinya akan membuat sistem crash. Dibatalkan.";
                return;
            }

            DialogResult r = MessageBox.Show(
                _form,
                "Akhiri proses '" + it.Name + "' (PID " + it.Id + ")?\n\n" +
                "Data yang belum disimpan di aplikasi tersebut akan HILANG.\n" +
                "Proses tidak diberi kesempatan menyimpan apa pun.",
                "Akhiri Proses",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) { return; }

            // Diverifikasi ulang SETELAH dialog: selama dialog terbuka proses bisa
            // keluar dan PID-nya dipakai proses lain.
            string err;
            Process p = ResolveVerified(it, out err);
            if (p == null)
            {
                _lblStatus.Text = err;
                return;
            }

            try
            {
                p.Kill();
                p.WaitForExit(3000);
                _lblStatus.Text = "Proses '" + it.Name + "' (PID " + it.Id + ") diakhiri.";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Gagal mengakhiri '" + it.Name + "': " + ex.Message;
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }

            UpdateDisplay(true);
        }

        // ===================== Restart Explorer =====================

        private static void RestartExplorer()
        {
            if (_isCleaning) { return; }

            DialogResult r = MessageBox.Show(
                _form,
                "Restart Windows Explorer?\n\n" +
                "Taskbar & desktop hilang sebentar (2-5 detik).\n" +
                "Jendela File Explorer yang terbuka akan tertutup.\n" +
                "Aplikasi lain TIDAK terpengaruh.",
                "Restart Explorer",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) { return; }

            _isCleaning = true;
            SetBusy(true);

            Thread worker = new Thread((ThreadStart)delegate { RestartExplorerWorker(); });
            worker.IsBackground = true;
            worker.Name = "RestartExplorerWorker";
            worker.Start();
        }

        private static void RestartExplorerWorker()
        {
            string result;
            try
            {
                Status("Mematikan explorer.exe...");
                KillExplorerInSession();

                // Windows biasanya menjalankan ulang shell sendiri (AutoRestartShell).
                // Tunggu sampai 6 detik sebelum turun ke jalur manual.
                Status("Menunggu Windows menjalankan ulang shell...");
                bool back = false;
                for (int i = 0; i < 12; i++)
                {
                    Thread.Sleep(500);
                    if (CountExplorerInSession() > 0) { back = true; break; }
                }

                if (!back)
                {
                    Status("Menjalankan ulang explorer.exe secara manual...");
                    string err = StartExplorer();
                    if (err != null)
                    {
                        result = "Gagal menjalankan ulang explorer: " + err
                            + "\nBuka manual: Ctrl+Shift+Esc > Run new task > explorer.exe";
                    }
                    else
                    {
                        Thread.Sleep(1200);
                        back = CountExplorerInSession() > 0;
                        result = back
                            ? "Explorer berhasil direstart. Memori shell kembali ke ukuran awal."
                            : "Explorer belum muncul. Buka manual: Ctrl+Shift+Esc > Run new task > explorer.exe";
                    }
                }
                else
                {
                    result = "Explorer berhasil direstart. Memori shell kembali ke ukuran awal.";
                }
            }
            catch (Exception ex)
            {
                result = "Terjadi kesalahan: " + ex.Message;
            }

            string captured = result;
            _form.BeginInvoke(new Action(delegate
            {
                _lblStatus.Text = captured;
                UpdateDisplay(true);
                _isCleaning = false;
                SetBusy(false);
            }));
        }

        // Hanya shell di sesi kita. Process.GetProcessesByName cakupannya
        // seluruh mesin, jadi tanpa filter ini shell user lain (RDP / fast user
        // switching) juga ikut dimatikan.
        private static void KillExplorerInSession()
        {
            Process[] ps = Process.GetProcessesByName("explorer");
            foreach (Process p in ps)
            {
                try
                {
                    if (p.SessionId == _ownSessionId) { p.Kill(); }
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        private static int CountExplorerInSession()
        {
            int n = 0;
            Process[] ps = Process.GetProcessesByName("explorer");
            foreach (Process p in ps)
            {
                try
                {
                    if (p.SessionId == _ownSessionId) { n++; }
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return n;
        }

        private static string StartExplorer()
        {
            // explorer.exe ada di %WINDIR%, BUKAN di System32.
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string exe = Path.Combine(winDir, "explorer.exe");
            try
            {
                Process p = Process.Start(new ProcessStartInfo
                {
                    FileName = File.Exists(exe) ? exe : "explorer.exe",
                    UseShellExecute = true
                });
                if (p != null) { p.Dispose(); }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ===================== Status helper =====================

        // Dipanggil dari thread pembersih.
        private static void Status(string text)
        {
            string captured = text;
            _form.BeginInvoke(new Action(delegate { _lblStatus.Text = captured; }));
        }

        private static void SetBusy(bool busy)
        {
            _btnClean.Enabled = !busy;
            _btnRestartExplorer.Enabled = !busy;
            if (busy && _procMenu != null) { _procMenu.Close(); }
        }
    }
}
