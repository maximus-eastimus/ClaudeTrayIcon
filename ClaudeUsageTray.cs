using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeUsageTray
{
    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            _mutex = new Mutex(true, "ClaudeUsageTray_SingleInstance_8f1c", out createdNew);
            if (!createdNew)
                return; // already running

            // Use system TLS (TLS 1.2) for the HTTPS call.
            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; }
            catch { }

            // Never let a stray exception pop the WinForms crash dialog for a
            // background tray app — swallow and keep running.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => { };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var ctx = new TrayContext())
                Application.Run(ctx);

            GC.KeepAlive(_mutex);
        }
    }

    /// <summary>One slice of usage data (a 5h, 7d, or per-model window).</summary>
    internal sealed class Window
    {
        public double Utilization;        // percent, e.g. 51.0
        public DateTimeOffset? ResetsAt;   // UTC reset time

        public string ResetText()
        {
            if (ResetsAt == null) return "n/a";
            TimeSpan left = ResetsAt.Value - DateTimeOffset.UtcNow;
            if (left.TotalSeconds <= 0) return "now";
            if (left.TotalDays >= 1) return string.Format("{0}d {1:00}h", (int)left.TotalDays, left.Hours);
            if (left.TotalHours >= 1) return string.Format("{0}h {1:00}m", (int)left.TotalHours, left.Minutes);
            return string.Format("{0}m", Math.Max(1, (int)left.TotalMinutes));
        }
    }

    internal sealed class Usage
    {
        public string Plan = "";
        public Window FiveHour;
        public Window SevenDay;
        public List<KeyValuePair<string, Window>> Models = new List<KeyValuePair<string, Window>>();
        public string ExtraText; // null unless extra usage enabled
    }

    /// <summary>OAuth credentials read from ~/.claude/.credentials.json.</summary>
    internal sealed class Creds
    {
        public string AccessToken;
        public string RefreshToken;
        public long ExpiresAtMs;
        public string Plan = "";
        public bool Expired(long bufferMs)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= (ExpiresAtMs - bufferMs);
        }
    }

    /// <summary>One poll attempt, shown in the tray menu history.</summary>
    public sealed class HistoryEntry
    {
        public string When { get; set; }  // "yyyy-MM-dd HH:mm:ss"
        public bool Ok { get; set; }
        public string Code { get; set; }  // "200", "401", "no login", "error", ...

        public string Line()
        {
            return string.Format("{0}  {1}   ·   {2}", Ok ? "✓" : "✗", Code, When);
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
        // Public OAuth client id used by Claude Code (not a secret).
        private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "ClaudeUsageTray";
        private static readonly int PollMs = 5 * 60 * 1000; // 5 minutes
        private static readonly int RetryMs = 30 * 1000;     // quick retry after an error
        // Refresh the token slightly before it actually expires.
        private const long ExpiryBufferMs = 120 * 1000;

        // After a 429 on the refresh endpoint, don't try again until this time.
        private DateTime _refreshBlockedUntil = DateTime.MinValue;

        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly System.Windows.Forms.Timer _timer;
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        private readonly string _credPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        private readonly string _markerPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageTray", ".installed");
        private readonly string _logPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageTray", "refresh.log");
        private readonly string _historyPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageTray", "history.json");

        private const int HistoryMax = 10;
        private List<HistoryEntry> _history = new List<HistoryEntry>();

        private Usage _last;
        private string _error;
        private DateTime _lastUpdate;

        private IntPtr _prevHicon = IntPtr.Zero;
        private Icon _prevIcon;

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

        public TrayContext()
        {
            _menu = new ContextMenuStrip();
            _icon = new NotifyIcon
            {
                Visible = true,
                Text = "Claude Usage — loading…",
                ContextMenuStrip = _menu
            };
            _icon.MouseClick += OnIconClick;

            LoadHistory();
            DrawIcon(); // initial empty box
            BuildMenu();

            EnsureFirstRunAutostart();

            _timer = new System.Windows.Forms.Timer { Interval = PollMs };
            _timer.Tick += async (s, e) => await PollAsync();
            _timer.Start();

            // first poll right away
            var _ = PollAsync();
        }

        // ---------- polling ----------

        private async Task PollAsync()
        {
            try
            {
                Creds c = TryReadCreds();
                if (c == null)
                {
                    _error = "Not logged into Claude Code";
                    AddHistory(false, "no login");
                    SetRetry();
                    RefreshUi();
                    return;
                }

                // If the stored token is expired/near-expiry, try to refresh it
                // ourselves (covers the case where Claude Code isn't running).
                if (c.Expired(ExpiryBufferMs))
                    c = await EnsureFreshAsync(c);

                int status = await TryUsageAsync(c.AccessToken, c.Plan);

                // A 401 with a token we thought was valid means it was revoked
                // (e.g. Claude Code rotated it) — refresh once and retry.
                if (status == 401)
                {
                    c = await EnsureFreshAsync(c);
                    status = await TryUsageAsync(c.AccessToken, c.Plan);
                }

                if (status == 200)
                {
                    _error = null;
                    _lastUpdate = DateTime.Now;
                    AddHistory(true, "200");
                    SetNormalInterval();
                }
                else
                {
                    _error = "API " + status;
                    AddHistory(false, status.ToString());
                    SetRetry();
                }
            }
            catch (Exception ex)
            {
                _error = "Update failed: " + ex.Message;
                AddHistory(false, "error");
                SetRetry();
            }
            RefreshUi();
        }

        /// <summary>Calls the usage endpoint; returns the HTTP status (200 on success, parsing data as a side effect).</summary>
        private async Task<int> TryUsageAsync(string token, string plan)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                using (var resp = await _http.SendAsync(req))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode)
                        _last = Parse(body, plan);
                    return (int)resp.StatusCode;
                }
            }
        }

        /// <summary>
        /// Returns credentials with a usable access token. Re-reads the file first
        /// (Claude Code may have refreshed it); only performs its own refresh if the
        /// token is still expired and we aren't in a 429 back-off window.
        /// </summary>
        private async Task<Creds> EnsureFreshAsync(Creds current)
        {
            // Claude Code may have already written a fresh token.
            Creds onDisk = TryReadCreds();
            if (onDisk != null && !onDisk.Expired(ExpiryBufferMs))
                return onDisk;

            Creds c = onDisk ?? current;
            if (string.IsNullOrEmpty(c.RefreshToken)) return c;
            if (DateTime.UtcNow < _refreshBlockedUntil) return c;

            try
            {
                var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", c.RefreshToken },
                    { "client_id", ClientId }
                });
                using (var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl))
                {
                    req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req))
                    {
                        if ((int)resp.StatusCode == 429)
                        {
                            _refreshBlockedUntil = DateTime.UtcNow.AddMinutes(2);
                            Log("refresh 429 — backing off 2m");
                            return c;
                        }
                        string respBody = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                        {
                            Log("refresh failed " + (int)resp.StatusCode);
                            return c;
                        }

                        var r = new JavaScriptSerializer().DeserializeObject(respBody) as Dictionary<string, object>;
                        if (r == null) return c;
                        string at = r.ContainsKey("access_token") ? Convert.ToString(r["access_token"]) : null;
                        string rt = r.ContainsKey("refresh_token") ? Convert.ToString(r["refresh_token"]) : null;
                        double exp = r.ContainsKey("expires_in") ? ToDouble(r["expires_in"]) : 0;

                        // Guard: only write back a complete, valid response. This makes
                        // an unexpected response shape a safe no-op (never corrupts the file).
                        if (string.IsNullOrEmpty(at) || string.IsNullOrEmpty(rt) || exp <= 0)
                        {
                            Log("refresh response missing fields — not writing");
                            return c;
                        }

                        long newExpiresAt = DateTimeOffset.UtcNow.AddSeconds(exp).ToUnixTimeMilliseconds();
                        if (WriteBackTokens(at, rt, newExpiresAt))
                        {
                            Log("refresh ok");
                            c.AccessToken = at; c.RefreshToken = rt; c.ExpiresAtMs = newExpiresAt;
                        }
                        return c;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("refresh exception: " + ex.Message);
                return c;
            }
        }

        private void SetRetry()
        {
            if (_timer != null && _timer.Interval != RetryMs) _timer.Interval = RetryMs;
        }

        private void SetNormalInterval()
        {
            if (_timer != null && _timer.Interval != PollMs) _timer.Interval = PollMs;
        }

        private Creds TryReadCreds()
        {
            if (!File.Exists(_credPath)) return null;
            try
            {
                string raw = File.ReadAllText(_credPath);
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(raw);
                var oauth = root["claudeAiOauth"] as Dictionary<string, object>;
                if (oauth == null) return null;
                var c = new Creds();
                if (oauth.ContainsKey("accessToken")) c.AccessToken = Convert.ToString(oauth["accessToken"]);
                if (oauth.ContainsKey("refreshToken")) c.RefreshToken = Convert.ToString(oauth["refreshToken"]);
                if (oauth.ContainsKey("expiresAt") && oauth["expiresAt"] != null)
                    c.ExpiresAtMs = Convert.ToInt64(oauth["expiresAt"], CultureInfo.InvariantCulture);
                if (oauth.ContainsKey("subscriptionType")) c.Plan = Convert.ToString(oauth["subscriptionType"]);
                return string.IsNullOrEmpty(c.AccessToken) ? null : c;
            }
            catch { return null; }
        }

        /// <summary>
        /// Rewrites only the three token fields in ~/.claude/.credentials.json,
        /// preserving every other field, via an atomic temp-file replace.
        /// </summary>
        private bool WriteBackTokens(string accessToken, string refreshToken, long expiresAtMs)
        {
            try
            {
                string raw = File.ReadAllText(_credPath);
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(raw);
                var oauth = root["claudeAiOauth"] as Dictionary<string, object>;
                if (oauth == null) return false;
                oauth["accessToken"] = accessToken;
                oauth["refreshToken"] = refreshToken;
                oauth["expiresAt"] = expiresAtMs;

                string outJson = new JavaScriptSerializer().Serialize(root);
                string tmp = _credPath + ".tmp";
                File.WriteAllText(tmp, outJson, new System.Text.UTF8Encoding(false));
                try { File.Replace(tmp, _credPath, null); }
                catch { File.Copy(tmp, _credPath, true); File.Delete(tmp); }
                return true;
            }
            catch (Exception ex)
            {
                Log("write-back failed: " + ex.Message);
                return false;
            }
        }

        private void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        // ---------- attempt history ----------

        private void AddHistory(bool ok, string code)
        {
            var e = new HistoryEntry
            {
                When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Ok = ok,
                Code = code
            };
            _history.Insert(0, e); // newest first
            if (_history.Count > HistoryMax) _history.RemoveRange(HistoryMax, _history.Count - HistoryMax);
            SaveHistory();
        }

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(_historyPath)) return;
                string raw = File.ReadAllText(_historyPath);
                var list = new JavaScriptSerializer().Deserialize<List<HistoryEntry>>(raw);
                if (list != null)
                {
                    _history = list;
                    if (_history.Count > HistoryMax)
                        _history.RemoveRange(HistoryMax, _history.Count - HistoryMax);
                }
            }
            catch { _history = new List<HistoryEntry>(); }
        }

        private void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_historyPath));
                File.WriteAllText(_historyPath, new JavaScriptSerializer().Serialize(_history),
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        private static Usage Parse(string json, string plan)
        {
            var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var u = new Usage { Plan = plan };
            u.FiveHour = ParseWindow(root, "five_hour");
            u.SevenDay = ParseWindow(root, "seven_day");

            string[] modelKeys = { "seven_day_opus", "seven_day_sonnet", "seven_day_cowork", "seven_day_oauth_apps" };
            foreach (var k in modelKeys)
            {
                var w = ParseWindow(root, k);
                if (w != null)
                {
                    string label = k.Replace("seven_day_", "").Replace("_", " ");
                    label = char.ToUpper(label[0]) + label.Substring(1);
                    u.Models.Add(new KeyValuePair<string, Window>(label, w));
                }
            }

            object exObj;
            if (root.TryGetValue("extra_usage", out exObj) && exObj is Dictionary<string, object>)
            {
                var ex = (Dictionary<string, object>)exObj;
                object en;
                if (ex.TryGetValue("is_enabled", out en) && en is bool && (bool)en)
                {
                    double util = ex.ContainsKey("utilization") ? ToDouble(ex["utilization"]) : 0;
                    u.ExtraText = string.Format("Extra usage: {0:0}%", util);
                }
            }
            return u;
        }

        private static Window ParseWindow(Dictionary<string, object> root, string key)
        {
            object o;
            if (!root.TryGetValue(key, out o) || !(o is Dictionary<string, object>)) return null;
            var d = (Dictionary<string, object>)o;
            var w = new Window();
            if (d.ContainsKey("utilization") && d["utilization"] != null)
                w.Utilization = ToDouble(d["utilization"]);
            if (d.ContainsKey("resets_at") && d["resets_at"] != null)
            {
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse(Convert.ToString(d["resets_at"]), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out dto))
                    w.ResetsAt = dto.ToUniversalTime();
            }
            return w;
        }

        private static double ToDouble(object o)
        {
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        // ---------- UI ----------

        private void RefreshUi()
        {
            try { DrawIcon(); } catch { }
            try { UpdateTooltip(); } catch { }
            try { BuildMenu(); } catch { }
        }

        private void UpdateTooltip()
        {
            // NOTE: NotifyIcon.Text on .NET Framework throws if length >= 64.
            string t;
            if (_last != null)
            {
                double s = _last.FiveHour != null ? _last.FiveHour.Utilization : 0;
                double w = _last.SevenDay != null ? _last.SevenDay.Utilization : 0;
                string sr = _last.FiveHour != null ? _last.FiveHour.ResetText() : "n/a";
                string wr = _last.SevenDay != null ? _last.SevenDay.ResetText() : "n/a";
                t = string.Format("Session {0:0}% · {1}   Week {2:0}% · {3}", s, sr, w, wr);
                if (_error != null) t = "(!) " + t;
            }
            else
            {
                t = _error ?? "Claude Usage — loading…";
            }
            _icon.Text = Truncate(t, 63);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private void BuildMenu()
        {
            _menu.Items.Clear();

            var header = new ToolStripMenuItem(_last != null
                ? "Claude Usage  (" + _last.Plan + ")"
                : "Claude Usage") { Enabled = false };
            header.Font = new Font(header.Font, FontStyle.Bold);
            _menu.Items.Add(header);
            _menu.Items.Add(new ToolStripSeparator());

            if (_last != null)
            {
                if (_last.FiveHour != null)
                    _menu.Items.Add(Info(string.Format("Session (5h):  {0:0}%   ·   resets in {1}",
                        _last.FiveHour.Utilization, _last.FiveHour.ResetText())));
                if (_last.SevenDay != null)
                    _menu.Items.Add(Info(string.Format("Week (7d):  {0:0}%   ·   resets in {1}",
                        _last.SevenDay.Utilization, _last.SevenDay.ResetText())));

                foreach (var m in _last.Models)
                    _menu.Items.Add(Info(string.Format("  {0} (7d):  {1:0}%   ·   resets in {2}",
                        m.Key, m.Value.Utilization, m.Value.ResetText())));

                if (_last.ExtraText != null)
                    _menu.Items.Add(Info("  " + _last.ExtraText));
            }
            else
            {
                _menu.Items.Add(Info(_error ?? "Loading…"));
            }

            _menu.Items.Add(new ToolStripSeparator());

            // "Last updated" line, with the attempt history as a submenu.
            string upd = _lastUpdate == default(DateTime) ? "—" : _lastUpdate.ToString("HH:mm:ss");
            var histParent = new ToolStripMenuItem(
                "Last updated: " + upd + (_error != null ? "   (!) " + _error : ""));
            var head = Info("Recent attempts (newest first):");
            head.Font = new Font(head.Font, FontStyle.Bold);
            histParent.DropDownItems.Add(head);
            histParent.DropDownItems.Add(new ToolStripSeparator());
            if (_history.Count == 0)
                histParent.DropDownItems.Add(Info("(no attempts yet)"));
            else
                foreach (var h in _history)
                    histParent.DropDownItems.Add(ColoredInfo(h.Line(), h.Ok));
            _menu.Items.Add(histParent);

            _menu.Items.Add(new ToolStripSeparator());

            var refresh = new ToolStripMenuItem("Refresh now");
            refresh.Click += async (s, e) => await PollAsync();
            _menu.Items.Add(refresh);

            var auto = new ToolStripMenuItem("Start with Windows") { Checked = IsAutostart(), CheckOnClick = true };
            auto.Click += (s, e) => SetAutostart(auto.Checked);
            _menu.Items.Add(auto);

            _menu.Items.Add(new ToolStripSeparator());

            var quit = new ToolStripMenuItem("Quit");
            quit.Click += (s, e) => ExitApp();
            _menu.Items.Add(quit);
        }

        private static ToolStripMenuItem Info(string text)
        {
            return new ToolStripMenuItem(text) { Enabled = false };
        }

        // Colored info line. Kept Enabled (with no click handler) because Windows
        // force-renders disabled items gray, ignoring ForeColor.
        private static ToolStripMenuItem ColoredInfo(string text, bool ok)
        {
            return new ToolStripMenuItem(text)
            {
                ForeColor = ok ? Color.FromArgb(0, 140, 0) : Color.FromArgb(200, 40, 40)
            };
        }

        private void OnIconClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Show the same menu on left-click, positioned by Windows.
                var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(_icon, null);
                else _menu.Show(Cursor.Position);
            }
        }

        // ---------- icon rendering ----------

        private void DrawIcon()
        {
            int sz = SystemInformation.SmallIconSize.Width;
            if (sz < 16) sz = 16;

            double sPct = (_last != null && _last.FiveHour != null) ? _last.FiveHour.Utilization : 0;
            double wPct = (_last != null && _last.SevenDay != null) ? _last.SevenDay.Utilization : 0;

            using (var bmp = new Bitmap(sz, sz))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.Clear(Color.Transparent);

                int leftW = (sz - 1) / 2;          // left bar width
                int rightX = leftW + 1;             // 1px divider gap
                int rightW = sz - rightX;

                // left = session (blue ~212), right = week (green ~140)
                DrawBar(g, 0, 0, leftW, sz, sPct, 212.0);
                DrawBar(g, rightX, 0, rightW, sz, wPct, 140.0);

                // error indicator: small red dot top-right
                if (_error != null && _last == null)
                    using (var br = new SolidBrush(Color.FromArgb(220, 200, 60, 60)))
                        g.FillRectangle(br, sz - 3, 0, 3, 3);

                SetIcon(bmp);
            }
        }

        private static void DrawBar(Graphics g, int x, int y, int w, int h, double pct, double hue)
        {
            if (w <= 0) return;
            // container background (subtle, visible on light & dark taskbars)
            using (var bg = new SolidBrush(Color.FromArgb(70, 130, 130, 130)))
                g.FillRectangle(bg, x, y, w, h);

            double frac = Math.Max(0, Math.Min(100, pct)) / 100.0;
            int fillH = (int)Math.Round(frac * h);
            if (fillH > 0)
            {
                // lighter at low usage, darker (but not too dark) at high usage
                double l = 0.82 - frac * 0.37; // 0.82 -> 0.45
                Color c = FromHsl(hue, 0.70, l);
                using (var br = new SolidBrush(c))
                    g.FillRectangle(br, x, y + (h - fillH), w, fillH);
            }

            // frame so the box shape is always visible
            using (var pen = new Pen(Color.FromArgb(170, 165, 165, 165)))
                g.DrawRectangle(pen, x, y, w - 1, h - 1);
        }

        private static Color FromHsl(double h, double s, double l)
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
            return Color.FromArgb(255,
                (int)Math.Round((r + m) * 255),
                (int)Math.Round((g + m) * 255),
                (int)Math.Round((b + m) * 255));
        }

        private void SetIcon(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            Icon ic = Icon.FromHandle(h);
            _icon.Icon = ic;

            // free the previous icon's resources now that it's swapped out
            if (_prevIcon != null) _prevIcon.Dispose();
            if (_prevHicon != IntPtr.Zero) DestroyIcon(_prevHicon);
            _prevIcon = ic;
            _prevHicon = h;
        }

        // ---------- autostart ----------

        private void EnsureFirstRunAutostart()
        {
            try
            {
                if (File.Exists(_markerPath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_markerPath));
                File.WriteAllText(_markerPath, DateTime.Now.ToString("o"));
                SetAutostart(true); // default ON for first run
            }
            catch { }
        }

        private static bool IsAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        private static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue);
                }
            }
            catch { }
        }

        private void ExitApp()
        {
            _timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            if (_prevIcon != null) _prevIcon.Dispose();
            if (_prevHicon != IntPtr.Zero) DestroyIcon(_prevHicon);
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _icon.Dispose(); _menu.Dispose(); _timer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
