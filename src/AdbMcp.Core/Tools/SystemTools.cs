using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AdbMcp.Adb;
using AdbMcp.Mcp;
using Newtonsoft.Json.Linq;

namespace AdbMcp.Tools
{
    /// <summary>
    /// send_sms — compose and (optionally) send an SMS. This is an irreversible,
    /// third-party-facing action, so it is confirmation-gated: without confirm=true
    /// it only previews. Uses the reliable intent-compose path (SENDTO + sms_body),
    /// then attempts to press the send button, which is the pragmatic approach the
    /// feasibility report recommends over the OEM-fragile direct service call.
    /// </summary>
    public sealed class SendSmsTool : ITool
    {
        public string Name => "send_sms";
        public string Description =>
            "Compose an SMS to a phone number and optionally send it. IRREVERSIBLE: requires confirm=true to " +
            "actually act (it previews otherwise). Composes via the messaging app (intent), then presses send.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["phone"] = Schema.StringProp("Recipient phone number, e.g. +15551234567."),
            ["message"] = Schema.StringProp("Message body."),
            ["send"] = Schema.BoolProp("Press the send button after composing. Default true. If false, leaves it composed for review."),
            ["confirm"] = Schema.BoolProp("Must be true to perform the action. Without it, the tool only previews."),
        }, "phone", "message");

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            string phone = Args.Str(arguments, "phone");
            string message = Args.Str(arguments, "message");
            if (string.IsNullOrWhiteSpace(phone) || message == null)
                return ToolResult.Error("send_sms requires 'phone' and 'message'.");

            bool send = Args.Bool(arguments, "send", true);
            bool confirm = Args.Bool(arguments, "confirm", false);

            string cleanPhone = Regex.Replace(phone, @"[^\d+*#]", "");

            if (ctx.Config.RequireConfirmation && !confirm)
            {
                return ToolResult.Text(
                    "CONFIRMATION REQUIRED — no action taken.\n" +
                    "Would " + (send ? "compose AND SEND" : "compose (not send)") + " an SMS:\n" +
                    "  To: " + cleanPhone + "\n" +
                    "  Body: " + message + "\n" +
                    "Re-call send_sms with confirm=true to proceed.");
            }

            // Compose via intent — reliable across OEMs; fills recipient + body in the SMS app.
            string amCmd = "am start -a android.intent.action.SENDTO -d " + Sq("sms:" + cleanPhone) +
                           " --es sms_body " + Sq(message) + " --ez exit_on_sent true";
            var compose = ctx.Adb.Shell(amCmd);
            if (!compose.Ok)
                throw new AdbException(compose.DescribeFailure("compose SMS intent"));

            if (!send)
                return ToolResult.Text("Composed SMS to " + cleanPhone + " (not sent; send=false). Review on device, " +
                                       "or call send_sms again with send=true, confirm=true.");

            // Give the messaging app a moment to render, then locate the send control.
            Thread.Sleep(1200);
            UiHierarchy hierarchy;
            try { hierarchy = Perception.DumpHierarchy(ctx.Adb); }
            catch (AdbException ex)
            {
                return ToolResult.Text("Composed SMS to " + cleanPhone + ", but could not read the screen to find the " +
                                       "send button (" + ex.Message + "). Press send manually or call press_key.");
            }

            // Send buttons are commonly labelled by content-desc "Send" or a send resource-id.
            var matches = hierarchy.Resolve(null, "send", "Send", clickableOnly: true);
            if (matches.Count == 0)
                matches = hierarchy.Resolve("Send", null, null, clickableOnly: true);

            if (matches.Count == 0)
                return ToolResult.Text("Composed SMS to " + cleanPhone + ", but no send button was found in the UI. " +
                                       "The messaging app may differ — use get_state then tap the send control.");

            var target = matches[0];
            var tap = ctx.Adb.Shell("input tap " + target.CenterX + " " + target.CenterY);
            if (!tap.Ok)
                throw new AdbException(tap.DescribeFailure("tap send button"));

            return ToolResult.Text("Sent SMS to " + cleanPhone + " (\"" + message + "\"). Pressed send at (" +
                                   target.CenterX + "," + target.CenterY + ").");
        }

        // Single-quote a value for the device shell (sh -c), escaping embedded single quotes.
        private static string Sq(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// shell — raw adb shell, guarded by the allowlist policy. Treated as a loaded
    /// weapon: only allowlisted commands run; unlisted ones are refused unless the
    /// operator enabled confirm-to-override.
    /// </summary>
    public sealed class ShellTool : ITool
    {
        public string Name => "shell";
        public string Description =>
            "Run a raw adb shell command. GUARDED: only commands matching the server's allowlist run. " +
            "Non-allowlisted commands are refused (or require confirm=true if the operator enabled override). " +
            "Prefer the specific tools; use this for read-only inspection the other tools don't cover.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["command"] = Schema.StringProp("The shell command to run on the device (without the leading 'adb shell')."),
            ["confirm"] = Schema.BoolProp("Required to run a non-allowlisted command when override is enabled."),
        }, "command");

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            string command = Args.Str(arguments, "command");
            if (string.IsNullOrWhiteSpace(command))
                return ToolResult.Error("shell requires 'command'.");

            var decision = ctx.ShellPolicy.Evaluate(command);
            if (!decision.Allowed)
                return ToolResult.Error(decision.Reason);

            if (decision.RequiresConfirmation && !Args.Bool(arguments, "confirm", false))
                return ToolResult.Text("CONFIRMATION REQUIRED — no action taken.\n" + decision.Reason +
                                       "\nCommand: " + command + "\nRe-call with confirm=true to run it.");

            var r = ctx.Adb.Shell(command);
            var sb = new StringBuilder();
            sb.Append("exit=").Append(r.ExitCode).Append('\n');
            var outText = r.StdOutText;
            if (!string.IsNullOrEmpty(outText)) sb.Append("stdout:\n").Append(Trunc(outText, 8000)).Append('\n');
            var err = r.StdErr?.Trim();
            if (!string.IsNullOrEmpty(err)) sb.Append("stderr:\n").Append(Trunc(err, 2000)).Append('\n');
            var result = ToolResult.Text(sb.ToString());
            result.IsError = !r.Ok;
            return result;
        }

        private static string Trunc(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "\n…(" + (s.Length - max) + " more chars truncated)";
    }

    /// <summary>start_mirror — launch the human-watchable scrcpy window.</summary>
    public sealed class StartMirrorTool : ITool
    {
        public string Name => "start_mirror";
        public string Description =>
            "Start the live, human-watchable scrcpy mirror window for the device. Requires scrcpy installed. " +
            "This is for a human observer; the agent still perceives via get_state / get_screenshot.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["max_size"] = Schema.IntProp("Optional scrcpy --max-size (longest edge in px) to cap mirror resolution."),
        });

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            try
            {
                var extra = new System.Collections.Generic.List<string>();
                int? maxSize = Args.Int(arguments, "max_size");
                if (maxSize.HasValue) { extra.Add("--max-size"); extra.Add(maxSize.Value.ToString()); }
                return ToolResult.Text(ctx.Mirror.Start(extra));
            }
            catch (Exception ex)
            {
                return ToolResult.Error(ex.Message);
            }
        }
    }

    /// <summary>stop_mirror — stop the scrcpy mirror window.</summary>
    public sealed class StopMirrorTool : ITool
    {
        public string Name => "stop_mirror";
        public string Description => "Stop the scrcpy mirror window if it is running.";
        public JObject InputSchema => Schema.Object(new JObject());

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
            => ToolResult.Text(ctx.Mirror.Stop());
    }

    /// <summary>wait — pause between observe/act steps (e.g. to let an animation settle).</summary>
    public sealed class WaitTool : ITool
    {
        public string Name => "wait";
        public string Description => "Pause for a number of seconds (max 30) to let the UI settle before the next observation.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["seconds"] = Schema.Prop("number", "Seconds to wait (0-30)."),
        }, "seconds");

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            double secs = 1.0;
            var t = arguments["seconds"];
            if (t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer))
                secs = t.Value<double>();
            if (secs < 0) secs = 0;
            if (secs > 30) secs = 30;
            Thread.Sleep((int)(secs * 1000));
            return ToolResult.Text("Waited " + secs + "s.");
        }
    }

    /// <summary>
    /// start_stream — establish the programmatic scrcpy session (H.264 video + control
    /// sockets). Once running, input tools route over the low-latency control channel and
    /// get_screenshot can sample live decoded frames.
    /// </summary>
    public sealed class StartStreamTool : ITool
    {
        public string Name => "start_stream";
        public string Description =>
            "Open a low-latency scrcpy stream: a held-open H.264 video socket (live frames the agent can " +
            "sample) plus a control socket that carries tap/type/swipe/keys without spawning adb each time. " +
            "Requires a scrcpy-server JAR matching the configured version. Falls back to screencap for frames " +
            "if H.264 decode is unavailable.";

        public JObject InputSchema => Schema.Object(new JObject
        {
            ["max_size"] = Schema.IntProp("Longest edge (px) of the streamed video. Default from config (1024)."),
        });

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            if (ctx.Stream == null) return ToolResult.Error("Streaming is not enabled in this server instance.");
            try { return ToolResult.Text(ctx.Stream.Start(Args.Int(arguments, "max_size"))); }
            catch (Exception ex) { return ToolResult.Error("Could not start scrcpy stream: " + ex.Message); }
        }
    }

    /// <summary>stop_stream — tear down the scrcpy streaming session.</summary>
    public sealed class StopStreamTool : ITool
    {
        public string Name => "stop_stream";
        public string Description => "Stop the low-latency scrcpy stream. Input reverts to adb shell and frames to screencap.";
        public JObject InputSchema => Schema.Object(new JObject());

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            if (ctx.Stream == null) return ToolResult.Error("Streaming is not enabled in this server instance.");
            return ToolResult.Text(ctx.Stream.Stop());
        }
    }

    /// <summary>stream_status — report the streaming session state.</summary>
    public sealed class StreamStatusTool : ITool
    {
        public string Name => "stream_status";
        public string Description => "Report scrcpy stream state: running, input transport, decode availability, frame counts.";
        public JObject InputSchema => Schema.Object(new JObject());

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            if (ctx.Stream == null) return ToolResult.Text("Streaming is not enabled in this server instance.");
            return ToolResult.Text(ctx.Stream.Status() + "\nInput transport now: " + ctx.InputTransport + ".");
        }
    }

    /// <summary>get_notifications — best-effort read of the active notifications.</summary>
    public sealed class GetNotificationsTool : ITool
    {
        public string Name => "get_notifications";
        public string Description => "Read a best-effort summary of the device's current notifications (title/text lines).";
        public JObject InputSchema => Schema.Object(new JObject());

        private static readonly Regex Line =
            new Regex(@"(android\.title|android\.text|tickerText)=(.+)", RegexOptions.Compiled);

        public ToolResult Invoke(JObject arguments, ToolContext ctx)
        {
            var r = ctx.Adb.Shell("dumpsys notification --noredact");
            if (!r.Ok)
            {
                r = ctx.Adb.Shell("dumpsys notification");
                if (!r.Ok) return ToolResult.Error(r.DescribeFailure("dumpsys notification"));
            }

            var sb = new StringBuilder();
            int count = 0;
            foreach (var raw in r.StdOutText.Split('\n'))
            {
                var m = Line.Match(raw.Trim());
                if (!m.Success) continue;
                string kind = m.Groups[1].Value.Replace("android.", "");
                string val = m.Groups[2].Value.Trim();
                if (val.Length == 0 || val == "null") continue;
                sb.Append(kind).Append(": ").Append(val.Length > 200 ? val.Substring(0, 200) : val).Append('\n');
                if (++count >= 60) break;
            }
            return ToolResult.Text(count == 0 ? "No readable notifications found." : sb.ToString());
        }
    }
}
