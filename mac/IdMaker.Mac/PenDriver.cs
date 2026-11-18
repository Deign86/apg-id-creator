// The pen tablet's driver on a Mac. XP-Pen's driver is an ordinary app that
// its installer puts in /Applications; without it the pen still moves the
// pointer, but macOS hears no pressure from it.

namespace IdMaker.Mac;

internal static class PenDriver
{
    // Is this the name of a pen tablet driver? XP-Pen's own, or another
    // maker's: two tablet drivers on one computer fight over the pen. The
    // same rule as the Windows installer's.
    internal static bool IsDriverName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("xp-pen") || lower.Contains("xppen") || lower.Contains("pentablet")
            || lower.Contains("wacom") || lower.Contains("huion");
    }

    /// <summary>Is a pen tablet driver installed on this Mac?</summary>
    public static bool Installed => InstalledUnder("/Applications");

    internal static bool InstalledUnder(string applications)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(applications).Any(entry => IsDriverName(Path.GetFileName(entry)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;       // a folder that cannot be read has no driver this app can see
        }
    }
}
