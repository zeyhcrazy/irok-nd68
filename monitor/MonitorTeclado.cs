// Monitor de conexão USB do iRok ND68 (somente leitura: consulta os hubs USB do Windows).
// Compilar: csc /target:winexe /out:MonitorTeclado.exe /win32manifest:app.manifest MonitorTeclado.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace MonitorTeclado
{
    static class Native
    {
        public static readonly Guid HubInterface = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8");

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, IntPtr ov);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        public const uint IOCTL_USB_GET_NODE_INFORMATION = 0x220408;
        public const uint IOCTL_USB_GET_NODE_CONNECTION_NAME = 0x220414;
        public const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448;

        public static List<string> HubPaths()
        {
            var list = new List<string>();
            Guid g = HubInterface;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == new IntPtr(-1)) return list;
            try
            {
                for (int i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data)) break;
                    int need;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, need, out need, IntPtr.Zero))
                            list.Add(Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4)));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }
    }

    enum PortState { NoDevice, Connected, Failed, Enumerating, Other }

    class PortReading
    {
        public PortState State;
        public ushort Vid, Pid;
        public int Speed;          // 0 LS, 1 FS, 2 HS, 3 SS
        public bool IsHub;
        public int MinInterval;    // menor bInterval entre endpoints interrupt IN
    }

    class Hub : IDisposable
    {
        public string Path;
        public bool IsRoot;
        public int Ports;
        public SafeFileHandle Handle;
        public PortReading[] Last;

        public static Hub Open(string path)
        {
            var h = Native.CreateFile(path, 0x40000000, 0x2, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var ni = new byte[76]; int r;
            if (!Native.DeviceIoControl(h, Native.IOCTL_USB_GET_NODE_INFORMATION, ni, ni.Length, ni, ni.Length, out r, IntPtr.Zero))
            { h.Dispose(); return null; }
            var hub = new Hub();
            hub.Path = path;
            hub.IsRoot = path.IndexOf("ROOT_HUB", StringComparison.OrdinalIgnoreCase) >= 0;
            hub.Ports = ni[6];
            hub.Handle = h;
            hub.Last = new PortReading[hub.Ports + 1];
            return hub;
        }

        public PortReading Read(int port)
        {
            var ci = new byte[35 + 11 * 32]; int r;
            BitConverter.GetBytes((uint)port).CopyTo(ci, 0);
            if (!Native.DeviceIoControl(Handle, Native.IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, ci, ci.Length, ci, ci.Length, out r, IntPtr.Zero))
                return null;
            var p = new PortReading();
            uint st = BitConverter.ToUInt32(ci, 31);
            p.State = st == 0 ? PortState.NoDevice : st == 1 ? PortState.Connected : st == 9 || st == 10 ? PortState.Enumerating
                : st == 2 || st == 3 ? PortState.Failed : PortState.Other;
            p.Vid = BitConverter.ToUInt16(ci, 12);
            p.Pid = BitConverter.ToUInt16(ci, 14);
            p.Speed = ci[23];
            p.IsHub = ci[24] != 0;
            uint pipes = BitConverter.ToUInt32(ci, 27);
            p.MinInterval = 0;
            for (int i = 0; i < pipes && i < 32; i++)
            {
                int o = 35 + i * 11;
                byte addr = ci[o + 2], attr = ci[o + 3], interval = ci[o + 6];
                if ((attr & 3) == 3 && (addr & 0x80) != 0 && interval > 0 && (p.MinInterval == 0 || interval < p.MinInterval))
                    p.MinInterval = interval;
            }
            return p;
        }

        public string ChildHubName(int port)
        {
            var buf = new byte[1024]; int r;
            BitConverter.GetBytes((uint)port).CopyTo(buf, 0);
            if (!Native.DeviceIoControl(Handle, Native.IOCTL_USB_GET_NODE_CONNECTION_NAME, buf, buf.Length, buf, buf.Length, out r, IntPtr.Zero))
                return null;
            return Encoding.Unicode.GetString(buf, 8, Math.Max(0, r - 8)).TrimEnd('\0');
        }

        public void Dispose() { if (Handle != null) Handle.Dispose(); }
    }

    class KeyboardLink
    {
        public Hub Hub;
        public int Port;
        public PortReading Reading;
    }

    class MainForm : Form
    {
        const ushort KbVid = 0x1CA6, KbPid = 0x052C;

        static readonly Color Base = ColorTranslator.FromHtml("#1A2130");
        static readonly Color Surface = ColorTranslator.FromHtml("#222B3C");
        static readonly Color Ink = ColorTranslator.FromHtml("#E8ECF3");
        static readonly Color Muted = ColorTranslator.FromHtml("#9AA6BA");
        static readonly Color HighSpeed = ColorTranslator.FromHtml("#6CCBFF");
        static readonly Color FullSpeed = ColorTranslator.FromHtml("#F4BE5C");
        static readonly Color Offline = ColorTranslator.FromHtml("#EE7A8B");

        readonly List<Hub> hubs = new List<Hub>();
        DateTime nextHubScan = DateTime.MinValue;
        KeyboardLink current;
        string currentKey;
        DateTime connectedSince;
        int drops, failures;

        readonly Panel band = new Panel();
        readonly Label lblTitle = new Label(), lblState = new Label(), lblSpeed = new Label(), lblMode = new Label();
        readonly Label valPort = new Label(), valSince = new Label(), valDrops = new Label(), valFailures = new Label();
        readonly ListBox log = new ListBox();
        readonly Button btnCopy = new Button();
        readonly Timer timer = new Timer();

        public MainForm()
        {
            Text = "Monitor do teclado iRok ND68";
            BackColor = Base;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(460, 560);
            MinimumSize = new Size(420, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Information;

            band.Dock = DockStyle.Left;
            band.Width = 6;

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 18) };

            var head = new Panel { Dock = DockStyle.Top, Height = 28 };
            lblTitle.Text = "iRok ND68";
            lblTitle.Font = new Font("Segoe UI Semibold", 11f);
            lblTitle.ForeColor = Muted;
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(0, 2);
            lblState.Dock = DockStyle.Right;
            lblState.AutoSize = false;
            lblState.Width = 200;
            lblState.TextAlign = ContentAlignment.MiddleRight;
            lblState.Font = new Font("Segoe UI Semibold", 10f);
            head.Controls.Add(lblTitle);
            head.Controls.Add(lblState);

            lblSpeed.Dock = DockStyle.Top;
            lblSpeed.Height = 84;
            lblSpeed.Font = new Font(FontExists("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI", 40f, FontStyle.Bold);
            lblSpeed.TextAlign = ContentAlignment.MiddleLeft;
            lblSpeed.Padding = new Padding(0);

            lblMode.Dock = DockStyle.Top;
            lblMode.Height = 30;
            lblMode.Font = new Font("Segoe UI", 11.5f);
            lblMode.ForeColor = Ink;

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 4, Height = 124, Padding = new Padding(0, 14, 0, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(grid, 0, "Porta", valPort);
            AddRow(grid, 1, "Conectado desde", valSince);
            AddRow(grid, 2, "Quedas", valDrops);
            AddRow(grid, 3, "Falhas ao conectar", valFailures);

            var logHead = new Panel { Dock = DockStyle.Top, Height = 40 };
            var lblLog = new Label { Text = "Histórico", AutoSize = true, Location = new Point(0, 14), ForeColor = Muted, Font = new Font("Segoe UI Semibold", 10f) };
            btnCopy.Text = "Copiar histórico";
            btnCopy.FlatStyle = FlatStyle.Flat;
            btnCopy.FlatAppearance.BorderColor = Muted;
            btnCopy.FlatAppearance.MouseOverBackColor = Surface;
            btnCopy.ForeColor = Ink;
            btnCopy.BackColor = Base;
            btnCopy.AutoSize = true;
            btnCopy.Dock = DockStyle.Right;
            btnCopy.Click += delegate { CopyLog(); };
            logHead.Controls.Add(lblLog);
            logHead.Controls.Add(btnCopy);

            log.Dock = DockStyle.Fill;
            log.BorderStyle = BorderStyle.None;
            log.BackColor = Surface;
            log.ForeColor = Ink;
            log.DrawMode = DrawMode.OwnerDrawFixed;
            log.ItemHeight = 26;
            log.IntegralHeight = false;
            log.DrawItem += DrawLogItem;

            body.Controls.Add(log);
            body.Controls.Add(logHead);
            body.Controls.Add(grid);
            body.Controls.Add(lblMode);
            body.Controls.Add(lblSpeed);
            body.Controls.Add(head);
            Controls.Add(body);
            Controls.Add(band);

            // tentativas que falham duram ~50 ms; o intervalo curto é o que permite contá-las
            timer.Interval = 25;
            timer.Tick += delegate { Poll(); };
            AddLog("Monitor iniciado");
            ShowDisconnected();
            Poll();
            timer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1;
            try { Native.DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch (Exception) { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            foreach (var h in hubs) h.Dispose();
            base.OnFormClosed(e);
        }

        static bool FontExists(string name)
        {
            using (var f = new Font(name, 10f)) return f.Name == name;
        }

        void AddRow(TableLayoutPanel grid, int row, string label, Label value)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            var l = new Label { Text = label, ForeColor = Muted, AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
            value.AutoSize = true;
            value.ForeColor = Ink;
            value.Margin = new Padding(0, 3, 0, 0);
            grid.Controls.Add(l, 0, row);
            grid.Controls.Add(value, 1, row);
        }

        void RefreshHubs()
        {
            var paths = Native.HubPaths();
            for (int i = hubs.Count - 1; i >= 0; i--)
                if (!paths.Contains(hubs[i].Path)) { hubs[i].Dispose(); hubs.RemoveAt(i); }
            foreach (var p in paths)
            {
                if (hubs.Exists(h => h.Path == p)) continue;
                var hub = Hub.Open(p);
                if (hub != null) hubs.Add(hub);
            }
        }

        void Poll()
        {
            if (DateTime.Now >= nextHubScan)
            {
                RefreshHubs();
                nextHubScan = DateTime.Now.AddSeconds(2);
            }
            KeyboardLink found = null;
            foreach (var hub in hubs)
            {
                for (int port = 1; port <= hub.Ports; port++)
                {
                    var r = hub.Read(port);
                    if (r == null) continue;
                    var prev = hub.Last[port];
                    hub.Last[port] = r;
                    if (r.State == PortState.Connected && r.Vid == KbVid && r.Pid == KbPid)
                        found = new KeyboardLink { Hub = hub, Port = port, Reading = r };
                    // dispositivo que começou a conectar e sumiu antes de se identificar
                    if (prev != null && (prev.State == PortState.Enumerating || prev.State == PortState.Failed) && r.State == PortState.NoDevice && current == null)
                    {
                        failures++;
                        AddLog(string.Format("Falha ao conectar: {0}", Where(hub, port)));
                    }
                }
            }

            string key = found == null ? null : found.Hub.Path + "|" + found.Port + "|" + found.Reading.Speed;
            if (key != currentKey)
            {
                if (found == null)
                {
                    drops++;
                    AddLog("Desconectou");
                    ShowDisconnected();
                }
                else
                {
                    connectedSince = DateTime.Now;
                    AddLog(string.Format("Conectou em {0}: {1}", SpeedName(found.Reading.Speed), Where(found.Hub, found.Port)));
                    ShowConnected(found);
                }
                current = found;
                currentKey = key;
            }
            valDrops.Text = drops.ToString();
            valFailures.Text = failures.ToString();
        }

        string Where(Hub hub, int port)
        {
            // "no PC" e não "na placa": uma placa USB PCIe também aparece como porta direta
            if (hub.IsRoot) return string.Format("direto no PC, porta {0}", port);
            foreach (var root in hubs)
            {
                if (!root.IsRoot) continue;
                for (int p = 1; p <= root.Ports; p++)
                {
                    var r = root.Last[p];
                    if (r == null || !r.IsHub) continue;
                    var name = root.ChildHubName(p);
                    if (name != null && hub.Path.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                        return string.Format("pelo hub (porta {0} do hub, hub na porta {1} do PC)", port, p);
                }
            }
            return string.Format("pelo hub, porta {0}", port);
        }

        static string SpeedName(int speed)
        {
            switch (speed)
            {
                case 2: return "High-Speed";
                case 1: return "Full-Speed";
                case 0: return "Low-Speed";
                default: return "SuperSpeed";
            }
        }

        static int MaxPolling(PortReading r)
        {
            int iv = r.MinInterval < 1 ? 1 : r.MinInterval;
            if (r.Speed >= 2) return 8000 >> Math.Min(iv - 1, 15);
            return 1000 / iv;
        }

        void ShowConnected(KeyboardLink k)
        {
            bool hs = k.Reading.Speed >= 2;
            Color c = hs ? HighSpeed : FullSpeed;
            band.BackColor = c;
            lblSpeed.ForeColor = c;
            lblSpeed.Text = hs ? "480 Mbps" : "12 Mbps";
            lblMode.Text = string.Format("{0}, polling máximo de {1} Hz", SpeedName(k.Reading.Speed), MaxPolling(k.Reading));
            lblState.Text = "Conectado";
            lblState.ForeColor = c;
            valPort.Text = Capitalize(Where(k.Hub, k.Port));
            valSince.Text = connectedSince.ToString("HH:mm:ss");
        }

        void ShowDisconnected()
        {
            band.BackColor = Offline;
            lblSpeed.ForeColor = Offline;
            lblSpeed.Text = "Sem conexão";
            lblMode.Text = "Ligue o teclado em uma porta USB";
            lblState.Text = "Desconectado";
            lblState.ForeColor = Offline;
            valPort.Text = "Nenhuma";
            valSince.Text = "—";
        }

        static string Capitalize(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
        }

        void AddLog(string text)
        {
            log.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "\t" + text);
            while (log.Items.Count > 500) log.Items.RemoveAt(log.Items.Count - 1);
        }

        void DrawLogItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var parts = ((string)log.Items[e.Index]).Split('\t');
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(selected ? ColorTranslator.FromHtml("#2D3950") : Surface))
                e.Graphics.FillRectangle(bg, e.Bounds);
            var timeRect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y, 70, e.Bounds.Height);
            var textRect = new Rectangle(e.Bounds.X + 82, e.Bounds.Y, e.Bounds.Width - 88, e.Bounds.Height);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics, parts[0], log.Font, timeRect, Muted, flags);
            TextRenderer.DrawText(e.Graphics, parts.Length > 1 ? parts[1] : "", log.Font, textRect, Ink, flags);
        }

        void CopyLog()
        {
            var sb = new StringBuilder();
            for (int i = log.Items.Count - 1; i >= 0; i--)
                sb.AppendLine(((string)log.Items[i]).Replace('\t', ' '));
            if (sb.Length > 0) Clipboard.SetText(sb.ToString());
            btnCopy.Text = "Histórico copiado";
            var t = new Timer { Interval = 1500 };
            t.Tick += delegate { btnCopy.Text = "Copiar histórico"; t.Dispose(); };
            t.Start();
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--snapshot")
            {
                Snapshot(args[1]);
                return;
            }
            Application.Run(new MainForm());
        }

        // Teste: abre a janela fora da tela por 2 s e salva uma imagem dela.
        static void Snapshot(string path)
        {
            var f = new MainForm();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.Show();
            var end = DateTime.Now.AddSeconds(2);
            while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(f.Handle, hdc, 2);
                    g.ReleaseHdc(hdc);
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            f.Close();
        }
    }
}
