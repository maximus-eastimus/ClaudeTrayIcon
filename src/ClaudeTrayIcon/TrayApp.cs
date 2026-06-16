using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;

namespace ClaudeTrayIcon
{
    public sealed class HistoryEntry
    {
        public string When { get; set; } = "";
        public bool Ok { get; set; }
        public string Code { get; set; } = "";
        // ✓/✗ are distinct glyph shapes, so success/failure is distinguishable even
        // though a Windows tray menu renders text monochrome (color emoji don't draw).
        public string Line() => $"{(Ok ? "✓" : "✗")}  {Code}   ·   {When}";
    }

    internal sealed class Win
    {
        public double Utilization;
        public DateTimeOffset? ResetsAt;
        public string ResetText()
        {
            if (ResetsAt == null) return "n/a";
            var left = ResetsAt.Value - DateTimeOffset.UtcNow;
            if (left.TotalSeconds <= 0) return "now";
            if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours:00}h";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes:00}m";
            return $"{Math.Max(1, (int)left.TotalMinutes)}m";
        }
    }

    internal sealed class Usage
    {
        public string Plan = "";
        public Win? FiveHour;
        public Win? SevenDay;
        public List<KeyValuePair<string, Win>> Models = new();
        public string? ExtraText;
    }

    internal sealed class Creds
    {
        public string? AccessToken;
        public string? RefreshToken;
        public long ExpiresAtMs;
        public string Plan = "";
        public bool Expired(long bufferMs) =>
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= (ExpiresAtMs - bufferMs);
    }

    internal sealed class TrayApp
    {
        private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
        private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"; // public Claude Code client id
        private const string KeychainService = "Claude Code-credentials";
        private const string AutostartId = "ClaudeTrayIcon";

        private const double PollIntervalSec = 5 * 60;
        private const double RetryIntervalSec = 30;
        private const long ExpiryBufferMs = 120 * 1000;
        private const int HistoryMax = 10;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        private readonly TrayIndicator _tray;
        private readonly UITimer _timer;

        private readonly string _credPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

        private Usage? _last;
        private string? _error;
        private DateTime _lastUpdate;
        private DateTime _refreshBlockedUntil = DateTime.MinValue;
        private bool _polling;
        private List<HistoryEntry> _history = new();
        private Image? _prevImage;

        public TrayApp()
        {
            LoadHistory();
            EnsureFirstRunAutostart();

            _tray = new TrayIndicator { Title = "Claude Usage — loading…" };
            RefreshUi();
            _tray.Show();

            _timer = new UITimer { Interval = PollIntervalSec };
            _timer.Elapsed += async (_, _) => await PollAsync();
            _timer.Start();

            _ = PollAsync();
        }

        // ---------- polling ----------

        private async Task PollAsync()
        {
            if (_polling) return;
            _polling = true;
            try { await DoPollAsync(); }
            catch (Exception ex) { _error = "Update failed: " + ex.Message; AddHistory(false, "error"); SetInterval(RetryIntervalSec); }
            finally { _polling = false; RefreshUi(); }
        }

        private async Task DoPollAsync()
        {
            Creds? c = ReadCreds();
            if (c == null)
            {
                _error = "Not logged into Claude Code";
                AddHistory(false, "no login");
                SetInterval(RetryIntervalSec);
                return;
            }

            if (c.Expired(ExpiryBufferMs))
                c = await EnsureFreshAsync(c);

            int status = await TryUsageAsync(c.AccessToken!, c.Plan);
            if (status == 401)
            {
                c = await EnsureFreshAsync(c);
                status = await TryUsageAsync(c.AccessToken!, c.Plan);
            }

            if (status == 200)
            {
                _error = null;
                _lastUpdate = DateTime.Now;
                AddHistory(true, "200");
                SetInterval(PollIntervalSec);
            }
            else
            {
                _error = "API " + status;
                AddHistory(false, status.ToString());
                SetInterval(RetryIntervalSec);
            }
        }

        private async Task<int> TryUsageAsync(string token, string plan)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            using var resp = await Http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
                _last = Parse(body, plan);
            return (int)resp.StatusCode;
        }

        private async Task<Creds> EnsureFreshAsync(Creds current)
        {
            Creds? onDisk = ReadCreds();
            if (onDisk != null && !onDisk.Expired(ExpiryBufferMs))
                return onDisk;

            Creds c = onDisk ?? current;
            if (string.IsNullOrEmpty(c.RefreshToken)) return c;
            if (DateTime.UtcNow < _refreshBlockedUntil) return c;

            try
            {
                var payload = new JsonObject
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = c.RefreshToken,
                    ["client_id"] = ClientId
                }.ToJsonString();

                using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                using var resp = await Http.SendAsync(req);

                if ((int)resp.StatusCode == 429)
                {
                    _refreshBlockedUntil = DateTime.UtcNow.AddMinutes(2);
                    Log("refresh 429 — backing off 2m");
                    return c;
                }
                string respBody = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) { Log("refresh failed " + (int)resp.StatusCode); return c; }

                using var doc = JsonDocument.Parse(respBody);
                var root = doc.RootElement;
                string? at = GetStr(root, "access_token");
                string? rt = GetStr(root, "refresh_token");
                double exp = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

                if (string.IsNullOrEmpty(at) || string.IsNullOrEmpty(rt) || exp <= 0)
                {
                    Log("refresh response missing fields — not writing");
                    return c;
                }

                long newExpiresAt = DateTimeOffset.UtcNow.AddSeconds(exp).ToUnixTimeMilliseconds();
                if (WriteBackTokens(at!, rt!, newExpiresAt))
                {
                    Log("refresh ok");
                    c.AccessToken = at; c.RefreshToken = rt; c.ExpiresAtMs = newExpiresAt;
                }
                return c;
            }
            catch (Exception ex) { Log("refresh exception: " + ex.Message); return c; }
        }

        private void SetInterval(double sec) { if (_timer != null && Math.Abs(_timer.Interval - sec) > 0.001) _timer.Interval = sec; }

        // ---------- credentials (cross-platform) ----------

        private string? ReadCredsRaw()
        {
            try { if (File.Exists(_credPath)) return File.ReadAllText(_credPath); }
            catch { }
            if (OperatingSystem.IsMacOS()) return KeychainRead();
            return null;
        }

        private Creds? ReadCreds()
        {
            string? raw = ReadCredsRaw();
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object) return null;
                var c = new Creds
                {
                    AccessToken = GetStr(o, "accessToken"),
                    RefreshToken = GetStr(o, "refreshToken"),
                    Plan = GetStr(o, "subscriptionType") ?? ""
                };
                if (o.TryGetProperty("expiresAt", out var ea) && ea.ValueKind == JsonValueKind.Number) c.ExpiresAtMs = ea.GetInt64();
                return string.IsNullOrEmpty(c.AccessToken) ? null : c;
            }
            catch { return null; }
        }

        private bool WriteBackTokens(string accessToken, string refreshToken, long expiresAtMs)
        {
            try
            {
                string? raw = ReadCredsRaw();
                if (string.IsNullOrEmpty(raw)) return false;
                var node = JsonNode.Parse(raw);
                var o = node?["claudeAiOauth"];
                if (o == null) return false;
                o["accessToken"] = accessToken;
                o["refreshToken"] = refreshToken;
                o["expiresAt"] = expiresAtMs;
                string outJson = node!.ToJsonString();

                if (File.Exists(_credPath))
                {
                    string tmp = _credPath + ".tmp";
                    File.WriteAllText(tmp, outJson, new UTF8Encoding(false));
                    try { File.Replace(tmp, _credPath, null); }
                    catch { File.Copy(tmp, _credPath, true); File.Delete(tmp); }
                    return true;
                }
                if (OperatingSystem.IsMacOS()) return KeychainWrite(outJson);
                return false;
            }
            catch (Exception ex) { Log("write-back failed: " + ex.Message); return false; }
        }

        private static string? KeychainRead()
        {
            var (code, outp, _) = Run("security", new[] { "find-generic-password", "-s", KeychainService, "-w" });
            return code == 0 && !string.IsNullOrWhiteSpace(outp) ? outp.Trim() : null;
        }

        private static bool KeychainWrite(string json)
        {
            string? acct = KeychainAccount();
            if (acct == null) return false;
            var (code, _, _) = Run("security", new[] { "add-generic-password", "-U", "-a", acct, "-s", KeychainService, "-w", json });
            return code == 0;
        }

        private static string? KeychainAccount()
        {
            var (code, outp, _) = Run("security", new[] { "find-generic-password", "-s", KeychainService });
            if (code != 0) return null;
            foreach (var line in outp.Split('\n'))
            {
                if (line.IndexOf("\"acct\"", StringComparison.Ordinal) < 0) continue;
                int q = line.IndexOf('"', line.IndexOf('=') + 1);
                int q2 = q >= 0 ? line.IndexOf('"', q + 1) : -1;
                if (q >= 0 && q2 > q) return line.Substring(q + 1, q2 - q - 1);
            }
            return null;
        }

        // ---------- history ----------

        private string DataDir()
        {
            string dir;
            if (OperatingSystem.IsWindows())
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeTrayIcon");
            else if (OperatingSystem.IsMacOS())
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ClaudeTrayIcon");
            else
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                dir = Path.Combine(xdg, "ClaudeTrayIcon");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void AddHistory(bool ok, string code)
        {
            _history.Insert(0, new HistoryEntry { When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Ok = ok, Code = code });
            if (_history.Count > HistoryMax) _history.RemoveRange(HistoryMax, _history.Count - HistoryMax);
            try { File.WriteAllText(Path.Combine(DataDir(), "history.json"), JsonSerializer.Serialize(_history)); } catch { }
        }

        private void LoadHistory()
        {
            try
            {
                string p = Path.Combine(DataDir(), "history.json");
                if (!File.Exists(p)) return;
                var list = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(p));
                if (list != null) _history = list.Count > HistoryMax ? list.GetRange(0, HistoryMax) : list;
            }
            catch { _history = new(); }
        }

        private void Log(string msg)
        {
            try { File.AppendAllText(Path.Combine(DataDir(), "refresh.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}"); }
            catch { }
        }

        // ---------- parsing ----------

        private static string? GetStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static Usage Parse(string json, string plan)
        {
            var u = new Usage { Plan = plan };
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            u.FiveHour = ParseWindow(root, "five_hour");
            u.SevenDay = ParseWindow(root, "seven_day");

            foreach (var k in new[] { "seven_day_opus", "seven_day_sonnet", "seven_day_cowork", "seven_day_oauth_apps" })
            {
                var w = ParseWindow(root, k);
                if (w != null)
                {
                    string label = k.Replace("seven_day_", "").Replace("_", " ");
                    label = char.ToUpper(label[0]) + label.Substring(1);
                    u.Models.Add(new KeyValuePair<string, Win>(label, w));
                }
            }

            if (root.TryGetProperty("extra_usage", out var ex) && ex.ValueKind == JsonValueKind.Object
                && ex.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.True)
            {
                double util = ex.TryGetProperty("utilization", out var uu) && uu.ValueKind == JsonValueKind.Number ? uu.GetDouble() : 0;
                u.ExtraText = $"Extra usage: {util:0}%";
            }
            return u;
        }

        private static Win? ParseWindow(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var o) || o.ValueKind != JsonValueKind.Object) return null;
            var w = new Win();
            if (o.TryGetProperty("utilization", out var ut) && ut.ValueKind == JsonValueKind.Number) w.Utilization = ut.GetDouble();
            if (o.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ra.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                w.ResetsAt = dto.ToUniversalTime();
            return w;
        }

        // ---------- UI ----------

        private void RefreshUi()
        {
            void Update()
            {
                try
                {
                    var img = BuildIcon();
                    _tray.Image = img;
                    _prevImage?.Dispose();
                    _prevImage = img;
                }
                catch { }
                try { _tray.Title = Truncate(BuildTooltip(), 63); } catch { }
                try { _tray.Menu = BuildMenu(); } catch { }
            }
            if (Application.Instance != null) Application.Instance.Invoke(Update);
            else Update();
        }

        private string BuildTooltip()
        {
            if (_last == null) return _error ?? "Claude Usage — loading…";
            double s = _last.FiveHour?.Utilization ?? 0, w = _last.SevenDay?.Utilization ?? 0;
            string sr = _last.FiveHour?.ResetText() ?? "n/a", wr = _last.SevenDay?.ResetText() ?? "n/a";
            string t = $"Session {s:0}% · {sr}   Week {w:0}% · {wr}";
            return _error != null ? "(!) " + t : t;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Dis(_last != null ? $"Claude Usage  ({_last.Plan})" : "Claude Usage"));
            menu.Items.Add(new SeparatorMenuItem());

            if (_last != null)
            {
                if (_last.FiveHour != null) menu.Items.Add(Dis($"Session (5h):  {_last.FiveHour.Utilization:0}%   ·   resets in {_last.FiveHour.ResetText()}"));
                if (_last.SevenDay != null) menu.Items.Add(Dis($"Week (7d):  {_last.SevenDay.Utilization:0}%   ·   resets in {_last.SevenDay.ResetText()}"));
                foreach (var m in _last.Models) menu.Items.Add(Dis($"  {m.Key} (7d):  {m.Value.Utilization:0}%   ·   resets in {m.Value.ResetText()}"));
                if (_last.ExtraText != null) menu.Items.Add(Dis("  " + _last.ExtraText));
            }
            else menu.Items.Add(Dis(_error ?? "Loading…"));

            menu.Items.Add(new SeparatorMenuItem());

            string upd = _lastUpdate == default ? "—" : _lastUpdate.ToString("HH:mm:ss");
            var hist = new ButtonMenuItem { Text = "Last updated: " + upd + (_error != null ? "   (!) " + _error : "") };
            hist.Items.Add(Dis("Recent attempts (newest first):"));
            if (_history.Count == 0) hist.Items.Add(Dis("(no attempts yet)"));
            else foreach (var h in _history) hist.Items.Add(Dis(h.Line()));
            menu.Items.Add(hist);

            menu.Items.Add(new SeparatorMenuItem());

            var refresh = new ButtonMenuItem { Text = "Refresh now" };
            refresh.Click += async (_, _) => await PollAsync();
            menu.Items.Add(refresh);

            var auto = new CheckMenuItem { Text = "Start at login", Checked = IsAutostart() };
            auto.CheckedChanged += (_, _) => SetAutostart(auto.Checked);
            menu.Items.Add(auto);

            menu.Items.Add(new SeparatorMenuItem());

            var quit = new ButtonMenuItem { Text = "Quit" };
            quit.Click += (_, _) => Application.Instance.Quit();
            menu.Items.Add(quit);

            return menu;
        }

        private static ButtonMenuItem Dis(string text) => new() { Text = text, Enabled = false };

        // ---------- icon rendering ----------

        private Image BuildIcon()
        {
            const int sz = 32;
            double sPct = _last?.FiveHour?.Utilization ?? 0;
            double wPct = _last?.SevenDay?.Utilization ?? 0;

            var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppRgba);
            using (var g = new Graphics(bmp))
            {
                g.Clear(Colors.Transparent);
                int gap = 2;
                int leftW = (sz - gap) / 2;
                int rightX = leftW + gap;
                int rightW = sz - rightX;
                DrawBar(g, 0, 0, leftW, sz, sPct, 212);   // session = blue
                DrawBar(g, rightX, 0, rightW, sz, wPct, 140); // week = green
            }
            return bmp;
        }

        private static void DrawBar(Graphics g, int x, int y, int w, int h, double pct, double hue)
        {
            if (w <= 0) return;
            g.FillRectangle(new Color(0.51f, 0.51f, 0.51f, 0.275f), x, y, w, h);
            double frac = Math.Clamp(pct, 0, 100) / 100.0;
            int fillH = (int)Math.Round(frac * h);
            if (fillH > 0)
            {
                double l = 0.82 - frac * 0.37;
                g.FillRectangle(Hsl(hue, 0.70, l), x, y + (h - fillH), w, fillH);
            }
            g.DrawRectangle(new Pen(new Color(0.65f, 0.65f, 0.65f, 0.67f), 1), x, y, w - 1, h - 1);
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
            return new Color((float)(r + m), (float)(g + m), (float)(b + m));
        }

        // ---------- autostart (per-OS) ----------

        private static string ExePath() => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

        private void EnsureFirstRunAutostart()
        {
            try
            {
                string marker = Path.Combine(DataDir(), ".installed");
                if (File.Exists(marker)) return;
                File.WriteAllText(marker, DateTime.Now.ToString("o"));
                SetAutostart(true);
            }
            catch { }
        }

        private static string MacPlistPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.claudetrayicon.plist");
        private static string LinuxDesktopPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "claudetrayicon.desktop");

        private static bool IsAutostart()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                    return k?.GetValue(AutostartId) != null;
                }
                if (OperatingSystem.IsMacOS()) return File.Exists(MacPlistPath());
                return File.Exists(LinuxDesktopPath());
            }
            catch { return false; }
        }

        private static void SetAutostart(bool on)
        {
            try
            {
                string exe = ExePath();
                if (OperatingSystem.IsWindows())
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    if (k == null) return;
                    if (on) k.SetValue(AutostartId, "\"" + exe + "\"");
                    else if (k.GetValue(AutostartId) != null) k.DeleteValue(AutostartId);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    string p = MacPlistPath();
                    if (on)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                        File.WriteAllText(p,
                            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                            "<plist version=\"1.0\"><dict>\n" +
                            "  <key>Label</key><string>com.claudetrayicon</string>\n" +
                            "  <key>ProgramArguments</key><array><string>" + exe + "</string></array>\n" +
                            "  <key>RunAtLoad</key><true/>\n" +
                            "</dict></plist>\n");
                    }
                    else if (File.Exists(p)) File.Delete(p);
                }
                else
                {
                    string p = LinuxDesktopPath();
                    if (on)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                        File.WriteAllText(p, "[Desktop Entry]\nType=Application\nName=Claude Tray Icon\nExec=" + exe + "\nX-GNOME-Autostart-enabled=true\nTerminal=false\n");
                    }
                    else if (File.Exists(p)) File.Delete(p);
                }
            }
            catch { }
        }

        private static (int code, string stdout, string stderr) Run(string file, string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) return (-1, "", "");
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return (p.HasExited ? p.ExitCode : -1, o, e);
            }
            catch { return (-1, "", ""); }
        }
    }
}
