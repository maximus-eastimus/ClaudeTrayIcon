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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ClaudeTrayIcon
{
    public sealed class HistoryEntry
    {
        public string When { get; set; } = "";
        public bool Ok { get; set; }
        public string Code { get; set; } = "";
        public string Line() => $"{(Ok ? "🟢" : "🔴")}  {Code}   ·   {When}";
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

    internal sealed class TrayService
    {
        private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
        private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"; // public Claude Code client id
        private const string KeychainService = "Claude Code-credentials";       // macOS keychain item
        private const string AutostartId = "ClaudeTrayIcon";

        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
        private const long ExpiryBufferMs = 120 * 1000;
        private const int HistoryMax = 10;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        private readonly IClassicDesktopStyleApplicationLifetime _desktop;
        private readonly TrayIcon _icon;
        private readonly DispatcherTimer _timer;

        private readonly string _credPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

        private Usage? _last;
        private string? _error;
        private DateTime _lastUpdate;
        private DateTime _refreshBlockedUntil = DateTime.MinValue;
        private bool _polling;
        private List<HistoryEntry> _history = new();

        public TrayService(Application app, IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            LoadHistory();

            _icon = new TrayIcon
            {
                ToolTipText = "Claude Usage — loading…",
                IsVisible = true
            };
            _icon.Clicked += (_, _) => { /* left-click; menu opens via right-click natively */ };

            var icons = new TrayIcons { _icon };
            TrayIcon.SetIcons(app, icons);

            EnsureFirstRunAutostart();
            RefreshUi();

            _timer = new DispatcherTimer { Interval = PollInterval };
            _timer.Tick += async (_, _) => await PollAsync();
            _timer.Start();

            _ = PollAsync();
        }

        // ---------- polling ----------

        private async Task PollAsync()
        {
            if (_polling) return;          // re-entrancy guard (timer + "Refresh now")
            _polling = true;
            try
            {
                await DoPollAsync();
            }
            catch (Exception ex)
            {
                _error = "Update failed: " + ex.Message;
                AddHistory(false, "error");
                SetInterval(RetryInterval);
            }
            finally
            {
                _polling = false;
                RefreshUi();
            }
        }

        private async Task DoPollAsync()
        {
            Creds? c = ReadCreds();
            if (c == null)
            {
                _error = "Not logged into Claude Code";
                AddHistory(false, "no login");
                SetInterval(RetryInterval);
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
                SetInterval(PollInterval);
            }
            else
            {
                _error = "API " + status;
                AddHistory(false, status.ToString());
                SetInterval(RetryInterval);
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
            // Claude Code may have already refreshed it.
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
                if (!resp.IsSuccessStatusCode)
                {
                    Log("refresh failed " + (int)resp.StatusCode);
                    return c;
                }

                using var doc = JsonDocument.Parse(respBody);
                var root = doc.RootElement;
                string? at = GetStr(root, "access_token");
                string? rt = GetStr(root, "refresh_token");
                double exp = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

                // Guard: only write back a complete, valid response (never corrupts the store).
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
            catch (Exception ex)
            {
                Log("refresh exception: " + ex.Message);
                return c;
            }
        }

        private void SetInterval(TimeSpan ts)
        {
            if (_timer != null && _timer.Interval != ts) _timer.Interval = ts;
        }

        // ---------- credentials (cross-platform) ----------

        // Reads ~/.claude/.credentials.json, or the macOS Keychain if the file is absent.
        private string? ReadCredsRaw()
        {
            try
            {
                if (File.Exists(_credPath)) return File.ReadAllText(_credPath);
            }
            catch { /* fall through */ }
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
                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object)
                    return null;
                var c = new Creds
                {
                    AccessToken = GetStr(o, "accessToken"),
                    RefreshToken = GetStr(o, "refreshToken"),
                    Plan = GetStr(o, "subscriptionType") ?? ""
                };
                if (o.TryGetProperty("expiresAt", out var ea) && ea.ValueKind == JsonValueKind.Number)
                    c.ExpiresAtMs = ea.GetInt64();
                return string.IsNullOrEmpty(c.AccessToken) ? null : c;
            }
            catch { return null; }
        }

        // Rewrites only the three token fields, preserving every other field.
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
                if (OperatingSystem.IsMacOS())
                    return KeychainWrite(outJson);
                return false;
            }
            catch (Exception ex)
            {
                Log("write-back failed: " + ex.Message);
                return false;
            }
        }

        // ---------- macOS Keychain helpers ----------

        private static string? KeychainRead()
        {
            var (code, outp, _) = Run("security", new[] { "find-generic-password", "-s", KeychainService, "-w" });
            return code == 0 && !string.IsNullOrWhiteSpace(outp) ? outp.Trim() : null;
        }

        private static bool KeychainWrite(string json)
        {
            string? acct = KeychainAccount();
            if (acct == null) return false;
            var (code, _, _) = Run("security", new[]
            {
                "add-generic-password", "-U", "-a", acct, "-s", KeychainService, "-w", json
            });
            return code == 0;
        }

        private static string? KeychainAccount()
        {
            // Attributes are printed to stdout; the account is the `"acct"...="value"` line.
            var (code, outp, _) = Run("security", new[] { "find-generic-password", "-s", KeychainService });
            if (code != 0) return null;
            foreach (var line in outp.Split('\n'))
            {
                int i = line.IndexOf("\"acct\"", StringComparison.Ordinal);
                if (i < 0) continue;
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
                try { _icon.Icon = BuildIcon(); } catch { }
                try { _icon.ToolTipText = BuildTooltip(); } catch { }
                try { _icon.Menu = BuildMenu(); } catch { }
            }
            if (Dispatcher.UIThread.CheckAccess()) Update();
            else Dispatcher.UIThread.Post(Update);
        }

        private string BuildTooltip()
        {
            if (_last == null) return _error ?? "Claude Usage — loading…";
            double s = _last.FiveHour?.Utilization ?? 0, w = _last.SevenDay?.Utilization ?? 0;
            string sr = _last.FiveHour?.ResetText() ?? "n/a", wr = _last.SevenDay?.ResetText() ?? "n/a";
            string t = $"Session {s:0}% · {sr}   Week {w:0}% · {wr}";
            return _error != null ? "(!) " + t : t;
        }

        private NativeMenu BuildMenu()
        {
            var menu = new NativeMenu();
            menu.Add(new NativeMenuItem(_last != null ? $"Claude Usage  ({_last.Plan})" : "Claude Usage") { IsEnabled = false });
            menu.Add(new NativeMenuItemSeparator());

            if (_last != null)
            {
                if (_last.FiveHour != null)
                    menu.Add(Info($"Session (5h):  {_last.FiveHour.Utilization:0}%   ·   resets in {_last.FiveHour.ResetText()}"));
                if (_last.SevenDay != null)
                    menu.Add(Info($"Week (7d):  {_last.SevenDay.Utilization:0}%   ·   resets in {_last.SevenDay.ResetText()}"));
                foreach (var m in _last.Models)
                    menu.Add(Info($"  {m.Key} (7d):  {m.Value.Utilization:0}%   ·   resets in {m.Value.ResetText()}"));
                if (_last.ExtraText != null) menu.Add(Info("  " + _last.ExtraText));
            }
            else
            {
                menu.Add(Info(_error ?? "Loading…"));
            }

            menu.Add(new NativeMenuItemSeparator());

            string upd = _lastUpdate == default ? "—" : _lastUpdate.ToString("HH:mm:ss");
            var hist = new NativeMenuItem("Last updated: " + upd + (_error != null ? "   (!) " + _error : "")) { Menu = new NativeMenu() };
            hist.Menu.Add(Info("Recent attempts (newest first):"));
            hist.Menu.Add(new NativeMenuItemSeparator());
            if (_history.Count == 0) hist.Menu.Add(Info("(no attempts yet)"));
            else foreach (var h in _history) hist.Menu.Add(Info(h.Line()));
            menu.Add(hist);

            menu.Add(new NativeMenuItemSeparator());

            var refresh = new NativeMenuItem("Refresh now");
            refresh.Click += async (_, _) => await PollAsync();
            menu.Add(refresh);

            var auto = new NativeMenuItem("Start at login")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = IsAutostart()
            };
            auto.Click += (_, _) => { SetAutostart(!IsAutostart()); RefreshUi(); };
            menu.Add(auto);

            menu.Add(new NativeMenuItemSeparator());

            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => _desktop.Shutdown();
            menu.Add(quit);

            return menu;
        }

        private static NativeMenuItem Info(string text) => new(text) { IsEnabled = false };

        // ---------- icon rendering (Avalonia / Skia) ----------

        private WindowIcon BuildIcon()
        {
            const int sz = 64;
            double sPct = _last?.FiveHour?.Utilization ?? 0;
            double wPct = _last?.SevenDay?.Utilization ?? 0;

            var rtb = new RenderTargetBitmap(new PixelSize(sz, sz), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                int gap = 4;
                int leftW = (sz - gap) / 2;
                int rightX = leftW + gap;
                int rightW = sz - rightX;
                DrawBar(ctx, 0, 0, leftW, sz, sPct, 212);   // session = blue
                DrawBar(ctx, rightX, 0, rightW, sz, wPct, 140); // week = green
                if (_error != null && _last == null)
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 200, 60, 60)), null, new Rect(sz - 12, 0, 12, 12));
            }

            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;
            return new WindowIcon(ms);
        }

        private static void DrawBar(DrawingContext ctx, int x, int y, int w, int h, double pct, double hue)
        {
            if (w <= 0) return;
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 130, 130, 130)), null, new Rect(x, y, w, h));
            double frac = Math.Clamp(pct, 0, 100) / 100.0;
            int fillH = (int)Math.Round(frac * h);
            if (fillH > 0)
            {
                double l = 0.82 - frac * 0.37; // lighter when low, darker when high
                ctx.DrawRectangle(new SolidColorBrush(FromHsl(hue, 0.70, l)), null, new Rect(x, y + (h - fillH), w, fillH));
            }
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(170, 165, 165, 165)), 2), new Rect(x + 1, y + 1, w - 2, h - 2));
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
            return Color.FromArgb(255, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        // ---------- autostart (per-OS) ----------

        private static string ExePath() =>
            Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

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
                else // Linux
                {
                    string p = LinuxDesktopPath();
                    if (on)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                        File.WriteAllText(p,
                            "[Desktop Entry]\nType=Application\nName=Claude Tray Icon\n" +
                            "Exec=" + exe + "\nX-GNOME-Autostart-enabled=true\nTerminal=false\n");
                    }
                    else if (File.Exists(p)) File.Delete(p);
                }
            }
            catch { }
        }

        // ---------- process helper ----------

        private static (int code, string stdout, string stderr) Run(string file, string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(file)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
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
