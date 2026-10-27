// BatteryTime: mostra o tempo restante de bateria como texto na barra de tarefas, ao lado da bandeja.
// Compilar: csc /target:winexe /win32manifest:BatteryTime.manifest /out:BatteryTime.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll BatteryTime.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

static class BatteryTime
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new System.Threading.Mutex(true, "BatteryTimeTray", out created))
        {
            if (!created) return;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            var label = new TaskbarLabel();
            Application.Run();
            GC.KeepAlive(label);
        }
    }
}

// Janela filha da barra de tarefas, desenhada com transparência por pixel
class TaskbarLabel : NativeWindow
{
    [StructLayout(LayoutKind.Sequential)]
    struct SystemBatteryState
    {
        public byte AcOnLine, BatteryPresent, Charging, Discharging, Spare1, Spare2, Spare3, Tag;
        public uint MaxCapacity, RemainingCapacity;
        public int Rate; // mW, negativo descarregando
        public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

    [DllImport("powrprof.dll")]
    static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, out SystemBatteryState outBuf, uint outLen);
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    const int WM_RBUTTONUP = 0x205, WM_LBUTTONUP = 0x202;
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "BatteryTime";

    IntPtr taskbar;
    double smoothedRate = 0; // média móvel do consumo em mW
    string details = "";
    readonly Timer timer = new Timer { Interval = 2000 };
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly ToolStripMenuItem detailsItem = new ToolStripMenuItem { Enabled = false };

    public TaskbarLabel()
    {
        var autoStart = new ToolStripMenuItem("Iniciar com o Windows") { Checked = IsAutoStart() };
        autoStart.Click += (s, e) => { SetAutoStart(!autoStart.Checked); autoStart.Checked = IsAutoStart(); };
        menu.Items.Add(detailsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoStart);
        menu.Items.Add("Sair", null, (s, e) => Application.Exit());

        timer.Tick += (s, e) => Refresh();
        timer.Start();
        SystemEvents.UserPreferenceChanged += (s, e) => Refresh();
        SystemEvents.PowerModeChanged += (s, e) => { smoothedRate = 0; Refresh(); };
        Refresh();
    }

    // Recria a janela se o Explorer reiniciou e a barra de tarefas mudou
    void EnsureWindow()
    {
        IntPtr current = FindWindow("Shell_TrayWnd", null);
        if (current == taskbar && Handle != IntPtr.Zero) return;
        if (Handle != IntPtr.Zero) DestroyHandle();
        taskbar = current;
        if (taskbar == IntPtr.Zero) return;
        CreateHandle(new CreateParams
        {
            Parent = taskbar,
            Style = WS_CHILD | WS_VISIBLE,
            ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        });
    }

    void Refresh()
    {
        EnsureWindow();
        if (Handle == IntPtr.Zero) return;

        SystemBatteryState b;
        uint size = (uint)Marshal.SizeOf(typeof(SystemBatteryState));
        if (CallNtPowerInformation(5, IntPtr.Zero, 0, out b, size) != 0 || b.BatteryPresent == 0)
        {
            Draw("--", "sem bat.");
            details = "Sem bateria";
            return;
        }

        int percent = (int)Math.Round(SystemInformation.PowerStatus.BatteryLifePercent * 100);
        if (b.AcOnLine != 0)
        {
            smoothedRate = 0;
            bool charging = b.Charging != 0 && b.Rate > 0;
            Draw(percent + "%", charging ? "carregando" : "na tomada");
            details = charging ? string.Format("{0}% · carregando, {1:0.0} W", percent, b.Rate / 1000.0) : percent + "% · na tomada";
            return;
        }

        double rate = -b.Rate;
        if (rate <= 0)
        {
            Draw("...", percent + "%");
            details = percent + "% · calculando...";
            return;
        }
        smoothedRate = smoothedRate == 0 ? rate : smoothedRate * 0.95 + rate * 0.05;

        double minutes = b.RemainingCapacity / smoothedRate * 60;
        int hours = (int)(minutes / 60), mins = (int)(minutes % 60);
        Draw(string.Format("{0}:{1:00}", hours, mins), percent + "%");
        details = string.Format("{0}% · {1}h {2:00}min restantes · {3:0.0} W", percent, hours, mins, smoothedRate / 1000);
    }

    // Duas linhas, no estilo do relógio, à esquerda da bandeja
    void Draw(string line1, string line2)
    {
        RECT bar, tray;
        GetWindowRect(taskbar, out bar);
        IntPtr trayWnd = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayWnd == IntPtr.Zero || !GetWindowRect(trayWnd, out tray)) tray = bar;

        int height = bar.Bottom - bar.Top;
        float fontPx = height * 0.25f;
        using (var font = new Font("Segoe UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            Size s1 = TextRenderer.MeasureText(line1, font), s2 = TextRenderer.MeasureText(line2, font);
            int width = Math.Max(s1.Width, s2.Width) + 8;
            int lineHeight = (int)Math.Ceiling(font.GetHeight());

            using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // alfa mínimo para receber cliques
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var format = new StringFormat { Alignment = StringAlignment.Center };
                int top = (height - 2 * lineHeight) / 2;
                using (var brush = new SolidBrush(IsLightTaskbar() ? Color.Black : Color.White))
                {
                    g.DrawString(line1, font, brush, new RectangleF(0, top, width, lineHeight), format);
                    g.DrawString(line2, font, brush, new RectangleF(0, top + lineHeight, width, lineHeight), format);
                }

                int x = tray.Left - bar.Left - width - 4;
                SetWindowPos(Handle, IntPtr.Zero, x, 0, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                Present(bmp);
            }
        }
    }

    void Present(Bitmap bmp)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
        var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_ALPHA
        UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
        SelectObject(mem, old);
        DeleteObject(hbmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_RBUTTONUP || m.Msg == WM_LBUTTONUP)
        {
            detailsItem.Text = details;
            menu.Show(Cursor.Position);
        }
        base.WndProc(ref m);
    }

    static bool IsLightTaskbar()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            return key != null && Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0)) == 1;
    }

    static bool IsAutoStart()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue(AppName) != null;
    }

    static void SetAutoStart(bool enable)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enable) key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            else key.DeleteValue(AppName, false);
        }
    }
}
