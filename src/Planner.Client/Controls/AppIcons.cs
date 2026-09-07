using Avalonia;
using Avalonia.Media;

namespace Planner.Client.Controls;

/// <summary>Named access to the Lucide geometries in <c>Assets/Icons.axaml</c>.
///
/// View models pick their own icon (a nav entry knows it is "my issues"), and a resource key cannot be
/// resolved from a binding, so the lookup happens here instead of in XAML. Results are cached because
/// a board redraw would otherwise walk the resource dictionary once per row.</summary>
public static class AppIcons
{
    private static readonly Dictionary<string, Geometry?> Cache = [];

    public static Geometry? Get(string key)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            Geometry? geometry = null;

            if (Application.Current?.TryGetResource(key, null, out var resource) == true)
            {
                geometry = resource as Geometry;
            }

            // Null in the XAML previewer, where no Application is running. Cached either way: a missing
            // icon is a missing icon, and retrying per frame would not make it appear.
            Cache[key] = geometry;
            return geometry;
        }
    }

    public static Geometry? MyIssues => Get("IconCircleUser");
    public static Geometry? Board => Get("IconLayoutGrid");
    public static Geometry? Projects => Get("IconFolderKanban");
    public static Geometry? Project => Get("IconFolder");
    public static Geometry? Inbox => Get("IconInbox");
    public static Geometry? Menu => Get("IconMenu");
    public static Geometry? Plus => Get("IconPlus");
    public static Geometry? Check => Get("IconCheck");
    public static Geometry? Search => Get("IconSearch");
    public static Geometry? SignOut => Get("IconLogOut");
    public static Geometry? Refresh => Get("IconRefreshCw");
    public static Geometry? Team => Get("IconUsers");
    public static Geometry? ChevronDown => Get("IconChevronDown");
    public static Geometry? ChevronRight => Get("IconChevronRight");
    public static Geometry? Calendar => Get("IconCalendar");
    public static Geometry? Urgent => Get("IconTriangleAlert");
    public static Geometry? PriorityHigh => Get("IconSignalHigh");
    public static Geometry? PriorityMedium => Get("IconSignalMedium");
    public static Geometry? PriorityLow => Get("IconSignalLow");
    public static Geometry? Label => Get("IconTag");

    public static Geometry? Trash => Get("IconTrash");
    public static Geometry? Update => Get("IconCircleArrowUp");
    public static Geometry? Settings => Get("IconSettings");
}
