using System.Drawing;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

// Captures tight screenshots of the tray icon, its hover tooltip, the right-click
// menu, and the history fly-out — for the GitHub wiki. Saves PNGs to args[0].

var outDir = args.Length > 0 ? args[0] : ".";
System.IO.Directory.CreateDirectory(outDir);

using var automation = new UIA3Automation();
var desktop = automation.GetDesktop();

static string SafeName(AutomationElement e) { try { return e.Name ?? ""; } catch { return ""; } }
static string SafeClass(AutomationElement e) { try { return e.ClassName ?? ""; } catch { return ""; } }
static bool IsOurIcon(AutomationElement e)
{
    var n = SafeName(e);
    return (n.Contains("Session", System.StringComparison.OrdinalIgnoreCase) && n.Contains("Week", System.StringComparison.OrdinalIgnoreCase))
        || n.Contains("Claude Usage", System.StringComparison.OrdinalIgnoreCase);
}
static Rectangle Pad(Rectangle r, int p) => new(r.X - p, r.Y - p, r.Width + 2 * p, r.Height + 2 * p);
void Save(Rectangle r, string file)
{
    try { Capture.Rectangle(r).ToFile(System.IO.Path.Combine(outDir, file)); System.Console.WriteLine("SAVED " + file + "  " + r); }
    catch (System.Exception ex) { System.Console.WriteLine("FAIL " + file + ": " + ex.Message); }
}

// Reveal hidden icons.
var taskbar = desktop.FindAllChildren().FirstOrDefault(e => SafeClass(e) == "Shell_TrayWnd");
var chevron = taskbar?.FindAllDescendants().FirstOrDefault(e => SafeName(e).Contains("hidden", System.StringComparison.OrdinalIgnoreCase));
if (chevron != null) { try { chevron.Click(); System.Threading.Thread.Sleep(1200); } catch { } }

var icon = desktop.FindAllDescendants().FirstOrDefault(IsOurIcon);
if (icon == null) { System.Console.WriteLine("ICON_NOT_FOUND"); return; }
var ir = icon.BoundingRectangle;
System.Console.WriteLine("ICON at " + ir);

// 1) The tray icon (enlarged crop).
Save(Pad(ir, 10), "01-tray-icon.png");

// 2) Hover tooltip.
Mouse.MoveTo(new Point(ir.X + ir.Width / 2, ir.Y + ir.Height / 2));
System.Threading.Thread.Sleep(2200);
var tip = desktop.FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip)).FirstOrDefault(t => SafeName(t).Length > 5);
if (tip != null) Save(Pad(tip.BoundingRectangle, 6), "02-hover-tooltip.png");
else { Save(Pad(ir, 60), "02-hover-tooltip.png"); System.Console.WriteLine("(tooltip element not found; saved region)"); }

// 3) Right-click menu.
icon.RightClick();
System.Threading.Thread.Sleep(1300);
var quit = desktop.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)).FirstOrDefault(mi => SafeName(mi) == "Quit");
if (quit == null) { System.Console.WriteLine("MENU_NOT_SHOWN"); return; }
var menu = quit.Parent;
var mr = menu.BoundingRectangle;
Save(Pad(mr, 4), "03-right-click-menu.png");

// 4) History fly-out: hover the "Last updated" item to open its submenu.
var lastUpdated = menu.FindAllChildren(cf => cf.ByControlType(ControlType.MenuItem))
    .FirstOrDefault(mi => SafeName(mi).StartsWith("Last updated", System.StringComparison.OrdinalIgnoreCase));
if (lastUpdated != null)
{
    var lr = lastUpdated.BoundingRectangle;
    Mouse.MoveTo(new Point(lr.X + lr.Width / 2, lr.Y + lr.Height / 2));
    System.Threading.Thread.Sleep(1500);
    var sub = desktop.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
        .FirstOrDefault(mi => SafeName(mi).Contains("Recent attempts", System.StringComparison.OrdinalIgnoreCase)
                           || SafeName(mi).Contains("🟢") || SafeName(mi).Contains("🔴"));
    if (sub != null)
    {
        var sr = sub.Parent.BoundingRectangle;
        var union = Rectangle.Union(mr, sr);
        Save(Pad(union, 4), "04-history-flyout.png");
    }
    else { Save(Pad(mr, 4), "04-history-flyout.png"); System.Console.WriteLine("(submenu not found; saved menu)"); }
}

// dismiss the menu so it doesn't linger on screen
try { Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE); Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE); } catch { }
System.Console.WriteLine("DONE");
