using System.Collections.Generic;
using System.Text;
using AdbMcp.Adb;
using AdbMcp.Mcp;
using Newtonsoft.Json.Linq;

namespace AdbMcp.Tools
{
    /// <summary>
    /// tap — press an element or coordinate. Prefers element grounding: resolve by
    /// text / resource_id / content_desc against a fresh hierarchy so targeting
    /// survives resolution and layout changes. Raw x,y is the fallback.
    /// </summary>
    public sealed class TapTool : ITool
    {
        public string Name => "tap";
        public string Description =>
            "Tap the screen. Prefer targeting by text, resource_id, or content_desc (resolved against a live " +
            "hierarchy) over raw coordinates. You may also pass x,y directly, or index from the most recent " +
            "get_state snapshot. Set long_press=true for a long press.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["text"] = Schema.StringProp("Visible text or content-desc of the element to tap."),
            ["resource_id"] = Schema.StringProp("resource-id of the element (exact or partial)."),
            ["content_desc"] = Schema.StringProp("content-desc (accessibility label) of the element."),
            ["index"] = Schema.IntProp("Index into the interactable list from the latest get_state snapshot."),
            ["x"] = Schema.IntProp("Raw X pixel (use with y). Fallback when element targeting isn't possible."),
            ["y"] = Schema.IntProp("Raw Y pixel (use with x)."),
            ["long_press"] = Schema.BoolProp("Long-press instead of a tap. Default false."),
        });

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            bool longPress = Args.Bool(arguments, "long_press", false);
            int? x = Args.Int(arguments, "x");
            int? y = Args.Int(arguments, "y");

            if (x.HasValue && y.HasValue)
            {
                DoTap(ctx, x.Value, y.Value, longPress);
                return ToolResult.Text((longPress ? "Long-pressed" : "Tapped") + " at (" + x.Value + "," + y.Value + ").");
            }

            string text = Args.Str(arguments, "text");
            string rid = Args.Str(arguments, "resource_id");
            string desc = Args.Str(arguments, "content_desc");
            int? index = Args.Int(arguments, "index");

            if (text == null && rid == null && desc == null && !index.HasValue)
                return ToolResult.Error("tap needs one of: text, resource_id, content_desc, index, or x+y.");

            var hierarchy = Perception.DumpHierarchy(ctx.Adb);

            UiNode target;
            string how;
            int ambiguity = 0;

            if (index.HasValue)
            {
                var list = hierarchy.Interesting();
                if (index.Value < 0 || index.Value >= list.Count)
                    return ToolResult.Error("index " + index.Value + " is out of range (0.." + (list.Count - 1) +
                                            "). The screen may have changed — call get_state again.");
                target = list[index.Value];
                how = "index " + index.Value;
            }
            else
            {
                var matches = hierarchy.Resolve(text, rid, desc, clickableOnly: false);
                if (matches.Count == 0)
                    return ToolResult.Error("No element matched " + DescribeQuery(text, rid, desc) +
                                            ". Call get_state to see what's on screen.");
                target = matches[0];
                ambiguity = matches.Count;
                how = DescribeQuery(text, rid, desc);
            }

            DoTap(ctx, target.CenterX, target.CenterY, longPress);

            var sb = new StringBuilder();
            sb.Append(longPress ? "Long-pressed " : "Tapped ").Append(Describe(target))
              .Append(" at (").Append(target.CenterX).Append(',').Append(target.CenterY).Append(") via ").Append(how).Append('.');
            if (ambiguity > 1) sb.Append(" NOTE: ").Append(ambiguity).Append(" elements matched; tapped the first (topmost). Refine the query if wrong.");
            return ToolResult.Text(sb.ToString());
        }

        private static void DoTap(ToolContext ctx, int x, int y, bool longPress)
            => ctx.Tap(x, y, longPress); // routes over the scrcpy control channel when streaming

        private static string Describe(UiNode n)
        {
            if (!string.IsNullOrEmpty(n.Text)) return "\"" + n.Text + "\"";
            if (!string.IsNullOrEmpty(n.ContentDesc)) return "[" + n.ContentDesc + "]";
            if (!string.IsNullOrEmpty(n.ResourceId)) return "#" + n.ResourceId;
            return n.ShortClass;
        }

        private static string DescribeQuery(string text, string rid, string desc)
        {
            var parts = new List<string>();
            if (text != null) parts.Add("text=\"" + text + "\"");
            if (rid != null) parts.Add("resource_id=\"" + rid + "\"");
            if (desc != null) parts.Add("content_desc=\"" + desc + "\"");
            return string.Join(" ", parts);
        }
    }

    /// <summary>type_text — inject text into the focused field. Tap the field first.</summary>
    public sealed class TypeTextTool : ITool
    {
        public string Name => "type_text";
        public string Description =>
            "Type text into the currently focused input field. Tap the field first to focus it. " +
            "Set submit=true to press Enter afterwards (e.g. to run a search).";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["text"] = Schema.StringProp("The text to type."),
            ["submit"] = Schema.BoolProp("Press Enter after typing. Default false."),
        }, "text");

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            string text = Args.Str(arguments, "text");
            if (text == null) return ToolResult.Error("type_text requires 'text'.");

            ctx.TypeText(text); // routes over the scrcpy control channel when streaming

            var msg = new StringBuilder("Typed " + text.Length + " character(s) via " + ctx.InputTransport + ".");
            if (Args.Bool(arguments, "submit", false))
            {
                ctx.PressKey(66); // ENTER
                msg.Append(" Pressed Enter.");
            }
            return ToolResult.Text(msg.ToString());
        }
    }

    /// <summary>swipe — scroll/fling by direction, or an explicit two-point drag.</summary>
    public sealed class SwipeTool : ITool
    {
        public string Name => "swipe";
        public string Description =>
            "Swipe the screen. Either give a direction (up/down/left/right) to scroll, or explicit " +
            "x1,y1,x2,y2 coordinates. 'up' scrolls content upward (finger moves up).";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["direction"] = Schema.EnumProp("Scroll direction.", "up", "down", "left", "right"),
            ["distance_pct"] = Schema.IntProp("For direction swipes: travel as % of screen. Default 60."),
            ["x1"] = Schema.IntProp("Start X (explicit drag)."),
            ["y1"] = Schema.IntProp("Start Y."),
            ["x2"] = Schema.IntProp("End X."),
            ["y2"] = Schema.IntProp("End Y."),
            ["duration_ms"] = Schema.IntProp("Swipe duration in ms. Default 300."),
        });

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            int duration = Args.IntOr(arguments, "duration_ms", 300);

            int? x1 = Args.Int(arguments, "x1"), y1 = Args.Int(arguments, "y1");
            int? x2 = Args.Int(arguments, "x2"), y2 = Args.Int(arguments, "y2");

            if (x1.HasValue && y1.HasValue && x2.HasValue && y2.HasValue)
            {
                DoSwipe(ctx, x1.Value, y1.Value, x2.Value, y2.Value, duration);
                return ToolResult.Text("Swiped (" + x1 + "," + y1 + ") -> (" + x2 + "," + y2 + ") over " + duration + "ms.");
            }

            string dir = Args.Str(arguments, "direction");
            if (dir == null)
                return ToolResult.Error("swipe needs a direction, or all of x1,y1,x2,y2.");

            var size = DeviceQueries.GetScreenSize(ctx.Adb);
            if (!size.Known)
                return ToolResult.Error("Could not read screen size for a directional swipe; pass explicit x1,y1,x2,y2.");

            int pct = Args.IntOr(arguments, "distance_pct", 60);
            if (pct < 5) pct = 5; if (pct > 95) pct = 95;

            int cx = size.Width / 2, cy = size.Height / 2;
            int halfV = size.Height * pct / 200;  // half of the travel distance
            int halfH = size.Width * pct / 200;

            int sx = cx, sy = cy, ex = cx, ey = cy;
            switch (dir.ToLowerInvariant())
            {
                case "up": sy = cy + halfV; ey = cy - halfV; break;
                case "down": sy = cy - halfV; ey = cy + halfV; break;
                case "left": sx = cx + halfH; ex = cx - halfH; break;
                case "right": sx = cx - halfH; ex = cx + halfH; break;
                default: return ToolResult.Error("Unknown direction '" + dir + "'. Use up/down/left/right.");
            }

            DoSwipe(ctx, sx, sy, ex, ey, duration);
            return ToolResult.Text("Swiped " + dir + " (" + pct + "% of screen).");
        }

        private static void DoSwipe(ToolContext ctx, int x1, int y1, int x2, int y2, int duration)
            => ctx.Swipe(x1, y1, x2, y2, duration); // routes over the scrcpy control channel when streaming
    }

    /// <summary>press_key — send a hardware/navigation key by name or code.</summary>
    public sealed class PressKeyTool : ITool
    {
        public string Name => "press_key";
        public string Description =>
            "Press an Android key by friendly name (back, home, recents, enter, tab, del, up/down/left/right, " +
            "volume_up, power, search, …) or numeric keycode.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["key"] = Schema.StringProp("Key name (e.g. back, home, enter) or numeric keycode."),
        }, "key");

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            string key = Args.Str(arguments, "key");
            if (key == null) return ToolResult.Error("press_key requires 'key'.");

            int? code = AndroidKeys.Resolve(key);
            if (!code.HasValue)
                return ToolResult.Error("Unknown key '" + key + "'. Known names: " + string.Join(", ", AndroidKeys.KnownNames) + ".");

            ctx.PressKey(code.Value); // routes over the scrcpy control channel when streaming
            return ToolResult.Text("Pressed " + key + " (keycode " + code.Value + ") via " + ctx.InputTransport + ".");
        }
    }

    /// <summary>open_app — launch an app by package (and optional activity).</summary>
    public sealed class OpenAppTool : ITool
    {
        public string Name => "open_app";
        public string Description =>
            "Launch an app by package name. Optionally specify a fully-qualified activity; otherwise the " +
            "launcher activity is started.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["package"] = Schema.StringProp("App package name, e.g. com.android.settings."),
            ["activity"] = Schema.StringProp("Optional fully-qualified activity, e.g. com.app/.MainActivity."),
        }, "package");

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            string pkg = Args.Str(arguments, "package");
            if (string.IsNullOrWhiteSpace(pkg)) return ToolResult.Error("open_app requires 'package'.");

            string activity = Args.Str(arguments, "activity");
            string cmd = string.IsNullOrWhiteSpace(activity)
                ? "monkey -p " + pkg + " -c android.intent.category.LAUNCHER 1"
                : "am start -n " + (activity.Contains("/") ? activity : pkg + "/" + activity);

            var r = ctx.Adb.Shell(cmd);
            // monkey prints to stdout and returns 0 even when it can't find the app; detect that.
            string outText = r.StdOutText + " " + r.StdErr;
            if (!r.Ok || outText.IndexOf("No activities found", System.StringComparison.OrdinalIgnoreCase) >= 0
                      || outText.IndexOf("Error", System.StringComparison.OrdinalIgnoreCase) >= 0
                      || outText.IndexOf("aborted", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return ToolResult.Error("Could not launch " + pkg + ": " + outText.Trim());

            return ToolResult.Text("Launched " + pkg + (string.IsNullOrWhiteSpace(activity) ? "" : "/" + activity) + ".");
        }
    }
}
