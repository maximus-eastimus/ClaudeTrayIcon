using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

// Verifies the tray app's right-click menu works without a human watching:
// finds the notification-area icon by its (unique) tooltip, right-clicks it,
// and confirms the context menu appears with our items.

using var automation = new UIA3Automation();
var desktop = automation.GetDesktop();

static string SafeName(AutomationElement e) { try { return e.Name ?? ""; } catch { return ""; } }
static string SafeClass(AutomationElement e) { try { return e.ClassName ?? ""; } catch { return ""; } }

static bool IsOurIcon(AutomationElement e)
{
    var n = SafeName(e);
    return (n.Contains("Session", System.StringComparison.OrdinalIgnoreCase)
            && n.Contains("Week", System.StringComparison.OrdinalIgnoreCase))
        || n.Contains("Claude Usage", System.StringComparison.OrdinalIgnoreCase);
}

// Open the hidden-icons overflow so tray icons are in the UIA tree.
var taskbar = desktop.FindAllChildren().FirstOrDefault(e => SafeClass(e) == "Shell_TrayWnd");
var chevron = taskbar?.FindAllDescendants().FirstOrDefault(e =>
    SafeName(e).Contains("hidden", System.StringComparison.OrdinalIgnoreCase));
if (chevron != null) { try { chevron.Click(); System.Threading.Thread.Sleep(1200); } catch { } }

System.Console.WriteLine("--- candidate tray buttons ---");
foreach (var b in desktop.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
{
    var n = SafeName(b);
    if (n.Contains("Claude", System.StringComparison.OrdinalIgnoreCase)
        || n.Contains("Session", System.StringComparison.OrdinalIgnoreCase))
        System.Console.WriteLine($"  [{SafeClass(b)}] {n}");
}

var icon = desktop.FindAllDescendants().FirstOrDefault(IsOurIcon);
if (icon == null) { System.Console.WriteLine("RESULT: ICON_NOT_FOUND"); return; }
System.Console.WriteLine("FOUND_ICON: " + SafeName(icon));

icon.RightClick();
System.Threading.Thread.Sleep(1200);

var quit = desktop.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
    .FirstOrDefault(mi => SafeName(mi) == "Quit");
if (quit == null) { System.Console.WriteLine("RESULT: MENU_NOT_SHOWN (no Quit item found)"); return; }

var menu = quit.Parent;
var items = menu.FindAllChildren(cf => cf.ByControlType(ControlType.MenuItem));
System.Console.WriteLine($"RESULT: MENU_SHOWN with {items.Length} items");
foreach (var it in items)
{
    var n = SafeName(it);
    System.Console.WriteLine("  ITEM: " + (string.IsNullOrEmpty(n) ? "(separator)" : n));
}
