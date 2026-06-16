using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Renders the REAL UI pieces (the actual tray-icon bitmap and the actual WinForms
// ContextMenuStrip that Eto uses on Windows) on a clean background and captures
// just those, for the wiki. No desktop content is ever in frame.

internal static class ShotDemo
{
    [STAThread]
    private static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); // so Bounds == physical px
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 1) The tray icon, rendered large on a soft card so it's visible on any page.
        using (var card = IconCard(46, 25))
            card.Save(Path.Combine(outDir, "01-tray-icon.png"), ImageFormat.Png);

        // 2) Hover tooltip (the actual tooltip text in a tooltip-style window).
        CaptureTooltip("Session 46% · 2h 13m   Week 25% · 6d 05h", Path.Combine(outDir, "02-hover-tooltip.png"));

        // 3 & 4) The real context menu + history fly-out.
        CaptureMenus(outDir);

        Console.WriteLine("DONE");
    }

    // ---------- icon ----------

    private static Bitmap IconCard(double sPct, double wPct)
    {
        int icon = 96, pad = 22, card = icon + pad * 2;
        var bmp = new Bitmap(card, card, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using (var bg = new SolidBrush(Color.FromArgb(245, 245, 247)))
        using (var path = Rounded(new Rectangle(0, 0, card - 1, card - 1), 16))
            g.FillPath(bg, path);
        using (var ico = RenderIcon(icon, sPct, wPct))
            g.DrawImage(ico, pad, pad, icon, icon);
        return bmp;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, radius, radius, 180, 90);
        p.AddArc(r.Right - radius, r.Y, radius, radius, 270, 90);
        p.AddArc(r.Right - radius, r.Bottom - radius, radius, radius, 0, 90);
        p.AddArc(r.X, r.Bottom - radius, radius, radius, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Bitmap RenderIcon(int sz, double sPct, double wPct)
    {
        var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Color.Transparent);
        int gap = Math.Max(2, sz / 16);
        int leftW = (sz - gap) / 2, rightX = leftW + gap, rightW = sz - rightX;
        DrawBar(g, 0, 0, leftW, sz, sPct, 212);
        DrawBar(g, rightX, 0, rightW, sz, wPct, 140);
        return bmp;
    }

    private static void DrawBar(Graphics g, int x, int y, int w, int h, double pct, double hue)
    {
        if (w <= 0) return;
        using (var bg = new SolidBrush(Color.FromArgb(70, 130, 130, 130)))
            g.FillRectangle(bg, x, y, w, h);
        double frac = Math.Clamp(pct, 0, 100) / 100.0;
        int fillH = (int)Math.Round(frac * h);
        if (fillH > 0)
        {
            double l = 0.82 - frac * 0.37;
            using var br = new SolidBrush(Hsl(hue, 0.70, l));
            g.FillRectangle(br, x, y + (h - fillH), w, fillH);
        }
        using var pen = new Pen(Color.FromArgb(170, 165, 165, 165), Math.Max(1, w / 16f));
        g.DrawRectangle(pen, x, y, w - 1, h - 1);
    }

    private static Color Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromArgb(255, (int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    // ---------- capture helpers ----------

    private static void CaptureRect(Rectangle b, string path, int inflate = 0)
    {
        if (inflate != 0) b.Inflate(inflate, inflate); // only safe when the backdrop is the neutral host form
        using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        bmp.Save(path, ImageFormat.Png);
    }

    private static void CaptureTooltip(string text, string path)
    {
        using var tip = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(120, 120),
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = Color.FromArgb(252, 252, 252)
        };
        var lbl = new Label { AutoSize = true, Text = text, Location = new Point(10, 7), Font = new Font("Segoe UI", 9f) };
        tip.Controls.Add(lbl);
        tip.Paint += (_, e) => e.Graphics.DrawRectangle(Pens.Silver, 0, 0, tip.ClientSize.Width - 1, tip.ClientSize.Height - 1);
        tip.Show();
        Application.DoEvents();
        tip.ClientSize = new Size(lbl.Width + 20, lbl.Height + 14);
        tip.Invalidate();
        Application.DoEvents();
        Thread.Sleep(250);
        CaptureRect(tip.Bounds, path);
        tip.Close();
    }

    private static ToolStripMenuItem Dis(string t, Font? f = null)
    {
        var i = new ToolStripMenuItem(t) { Enabled = false };
        if (f != null) i.Font = f;
        return i;
    }

    private static void CaptureMenus(string outDir)
    {
        using var host = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60),
            Size = new Size(1200, 760),
            ShowInTaskbar = false,
            BackColor = Color.FromArgb(240, 240, 243)
        };
        host.Show();
        Application.DoEvents();

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var bold = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(Dis("Claude Usage  (pro)", bold));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Dis("Session (5h):  46%   ·   resets in 2h 13m"));
        menu.Items.Add(Dis("Week (7d):  25%   ·   resets in 6d 05h"));
        menu.Items.Add(new ToolStripSeparator());

        var hist = new ToolStripMenuItem("Last updated: 21:59:22");
        hist.DropDownItems.Add(Dis("Recent attempts (newest first):", bold));
        hist.DropDownItems.Add(new ToolStripSeparator());
        string[] rows = {
            "✓  200   ·   2026-06-16 21:59:22",
            "✓  200   ·   2026-06-16 21:54:22",
            "✓  200   ·   2026-06-16 21:49:22",
            "✗  401   ·   2026-06-16 21:44:18",
            "✓  200   ·   2026-06-16 21:39:22",
        };
        foreach (var r in rows) hist.DropDownItems.Add(Dis(r));
        menu.Items.Add(hist);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Refresh now"));
        menu.Items.Add(new ToolStripMenuItem("Start at login") { Checked = true });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit"));

        menu.Show(host, new Point(40, 40));
        Application.DoEvents();
        Thread.Sleep(350);
        var mb = menu.Bounds;
        CaptureRect(mb, Path.Combine(outDir, "03-right-click-menu.png"), 8);

        hist.ShowDropDown();
        Application.DoEvents();
        Thread.Sleep(350);
        var sb = hist.DropDown.Bounds;
        CaptureRect(Rectangle.Union(mb, sb), Path.Combine(outDir, "04-history-flyout.png"), 8);

        menu.Close();
        host.Close();
    }
}
