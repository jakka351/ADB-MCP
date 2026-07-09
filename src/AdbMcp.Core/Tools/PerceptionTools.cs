using System.Text;
using AdbMcp.Adb;
using AdbMcp.Imaging;
using AdbMcp.Mcp;
using Newtonsoft.Json.Linq;

namespace AdbMcp.Tools
{
    /// <summary>list_devices — enumerate attached devices/emulators.</summary>
    public sealed class ListDevicesTool : ITool
    {
        public string Name => "list_devices";
        public string Description => "List Android devices and emulators currently visible to adb, with state and model.";
        public JObject InputSchema => Schema.Object(new JObject());

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            var devices = ctx.Adb.ListDevices();
            if (devices.Count == 0)
                return ToolResult.Text("No devices found. Connect a device with USB debugging enabled, " +
                                       "or run 'adb tcpip 5555' + 'adb connect <ip>' for wireless.");
            var sb = new StringBuilder();
            sb.Append(devices.Count).Append(" device(s):\n");
            foreach (var d in devices) sb.Append("  ").Append(d).Append('\n');
            if (!string.IsNullOrEmpty(ctx.Adb.Serial)) sb.Append("Active target: ").Append(ctx.Adb.Serial).Append('\n');
            return ToolResult.Text(sb.ToString());
        }
    }

    /// <summary>
    /// get_state — the primary perception. Dumps the UI hierarchy into a compact,
    /// numbered element list with centres and flags, plus screen size and foreground app.
    /// Cheap and exact: prefer this over screenshots for deciding what to tap.
    /// </summary>
    public sealed class GetStateTool : ITool
    {
        public string Name => "get_state";
        public string Description =>
            "Read the current screen as a structured UI-hierarchy dump: a numbered list of on-screen " +
            "elements with text, resource-id, content-desc, interaction flags and tap centres, plus screen " +
            "size and foreground app. This is the cheapest, most exact perception — use it first.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["include_all"] = Schema.BoolProp("Include every node, not just interactable/labelled ones (verbose). Default false."),
            ["max_elements"] = Schema.IntProp("Cap on elements listed. Default 200."),
        });

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            bool includeAll = Args.Bool(arguments, "include_all", false);
            int max = Args.IntOr(arguments, "max_elements", 200);

            var size = DeviceQueries.GetScreenSize(ctx.Adb);
            var foreground = DeviceQueries.GetForeground(ctx.Adb);
            var hierarchy = Perception.DumpHierarchy(ctx.Adb);

            var sb = new StringBuilder();
            sb.Append("Screen: ").Append(size.ToString()).Append('\n');
            sb.Append("Foreground: ").Append(foreground).Append('\n');
            sb.Append("Elements (top-to-bottom, index is stable for this snapshot):\n");
            sb.Append(hierarchy.Describe(includeAll, max));
            sb.Append("\nTip: tap by text/resource_id/content_desc for resolution-independent targeting; " +
                      "use the numbered index only against this exact snapshot.");
            return ToolResult.Text(sb.ToString());
        }
    }

    /// <summary>
    /// get_screenshot — the sampled-frame tool (the "get_frame" of the design). Captures
    /// the screen, downscales/re-encodes for token economy, and returns it as an image.
    /// Sample this only when the hierarchy is ambiguous (canvas, games, images).
    /// </summary>
    public sealed class GetScreenshotTool : ITool
    {
        public string Name => "get_screenshot";
        public string Description =>
            "Capture the current screen as an image (sampled frame). Downscales and JPEG-encodes by default " +
            "to stay token-cheap. Use only when get_state's hierarchy is ambiguous — canvas views, games, " +
            "photos, or custom-rendered UI.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["format"] = Schema.EnumProp("Image format. Default from config (jpeg).", "jpeg", "png"),
            ["max_dimension"] = Schema.IntProp("Longest edge in px; frame is downscaled to fit. 0 = full resolution. Default from config."),
            ["quality"] = Schema.IntProp("JPEG quality 1-100. Default from config."),
            ["source"] = Schema.EnumProp("Frame source. 'auto' uses the live scrcpy stream when available, else screencap.", "auto", "stream", "screencap"),
        });

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            var cfg = ctx.Config;
            string format = Args.Str(arguments, "format", cfg.FrameFormat);
            int maxDim = Args.IntOr(arguments, "max_dimension", cfg.MaxFrameDimension);
            int quality = Args.IntOr(arguments, "quality", cfg.JpegQuality);
            string source = (Args.Str(arguments, "source", "auto") ?? "auto").ToLowerInvariant();

            EncodedFrame frame;
            string origin;

            bool wantStream = source != "screencap";
            if (wantStream && ctx.Stream != null && ctx.Stream.IsStreaming && ctx.Stream.HasLiveFrame)
            {
                using (var bmp = ctx.Stream.CaptureLiveFrame())
                {
                    if (bmp != null)
                    {
                        frame = FrameProcessor.Encode(bmp, format, maxDim, quality);
                        origin = "live scrcpy stream";
                    }
                    else { frame = ScreencapEncode(ctx, format, maxDim, quality); origin = "screencap"; }
                }
            }
            else if (source == "stream")
            {
                return ToolResult.Error("No live scrcpy frame is available. Start one with start_stream, " +
                                        "or use source=screencap / auto.");
            }
            else
            {
                frame = ScreencapEncode(ctx, format, maxDim, quality);
                origin = "screencap";
            }

            var note = "Frame " + frame.Width + "x" + frame.Height +
                       " (" + frame.MimeType + ", " + (frame.Data.Length / 1024) + " KB, " + origin + ")" +
                       (frame.Width != frame.SourceWidth
                           ? " downscaled from " + frame.SourceWidth + "x" + frame.SourceHeight
                           : "") + ".";

            var result = new ToolResult();
            result.AddText(note);
            result.AddImage(frame.Base64, frame.MimeType);
            return result;
        }

        private static EncodedFrame ScreencapEncode(ToolContext ctx, string format, int maxDim, int quality)
            => FrameProcessor.Encode(ctx.Adb.Screencap(), format, maxDim, quality);
    }
}
