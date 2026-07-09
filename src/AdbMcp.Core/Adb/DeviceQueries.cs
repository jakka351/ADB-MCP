using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AdbMcp.Adb
{
    /// <summary>Screen dimensions in device pixels.</summary>
    public struct ScreenSize
    {
        public int Width;
        public int Height;
        public bool Known => Width > 0 && Height > 0;
        public override string ToString() => Known ? Width + "x" + Height : "unknown";
    }

    /// <summary>Best-effort, OEM-tolerant device state probes used by get_state and swipe.</summary>
    public static class DeviceQueries
    {
        private static readonly Regex SizeRe =
            new Regex(@"(\d+)\s*x\s*(\d+)", RegexOptions.Compiled);

        /// <summary>Physical (override if present) screen size from <c>wm size</c>.</summary>
        public static ScreenSize GetScreenSize(AdbClient adb)
        {
            try
            {
                var text = adb.ShellText("wm size");
                // Output may include "Physical size: WxH" and "Override size: WxH".
                // Prefer the override (what apps actually render into) when present.
                ScreenSize physical = default, over = default;
                foreach (var lineRaw in text.Split('\n'))
                {
                    var line = lineRaw.Trim();
                    var m = SizeRe.Match(line);
                    if (!m.Success) continue;
                    var s = new ScreenSize
                    {
                        Width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                        Height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    };
                    if (line.StartsWith("Override", StringComparison.OrdinalIgnoreCase)) over = s;
                    else physical = s;
                }
                return over.Known ? over : physical;
            }
            catch (AdbException)
            {
                return default;
            }
        }

        /// <summary>Best-effort foreground activity / focused window string.</summary>
        public static string GetForeground(AdbClient adb)
        {
            // Try the resumed activity first (most reliable across recent Android).
            try
            {
                var r = adb.Shell("dumpsys activity activities");
                if (r.Ok)
                {
                    foreach (var lineRaw in r.StdOutText.Split('\n'))
                    {
                        var line = lineRaw.Trim();
                        if (line.StartsWith("mResumedActivity", StringComparison.Ordinal) ||
                            line.StartsWith("topResumedActivity", StringComparison.Ordinal) ||
                            line.StartsWith("ResumedActivity", StringComparison.Ordinal))
                        {
                            return ExtractComponent(line) ?? line;
                        }
                    }
                }
            }
            catch (AdbException) { }

            // Fallback: focused window.
            try
            {
                var r = adb.Shell("dumpsys window");
                if (r.Ok)
                {
                    foreach (var lineRaw in r.StdOutText.Split('\n'))
                    {
                        var line = lineRaw.Trim();
                        if (line.StartsWith("mCurrentFocus", StringComparison.Ordinal) ||
                            line.StartsWith("mFocusedApp", StringComparison.Ordinal))
                            return ExtractComponent(line) ?? line;
                    }
                }
            }
            catch (AdbException) { }

            return "(unknown)";
        }

        private static string ExtractComponent(string line)
        {
            // Pull the "package/.Activity" token if present.
            var m = Regex.Match(line, @"([A-Za-z0-9_.]+/[A-Za-z0-9_.$]+)");
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
