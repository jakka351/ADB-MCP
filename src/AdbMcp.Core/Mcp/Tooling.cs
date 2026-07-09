using System;
using System.Collections.Generic;
using AdbMcp.Adb;
using AdbMcp.Config;
using AdbMcp.Scrcpy;
using Newtonsoft.Json.Linq;

namespace AdbMcp.Mcp
{
    /// <summary>Shared services handed to every tool at invocation time.</summary>
    public sealed class ToolContext
    {
        public AdbClient Adb { get; }
        public ServerConfig Config { get; }
        public ScrcpyMirror Mirror { get; }
        public Safety.ShellPolicy ShellPolicy { get; }

        /// <summary>Optional scrcpy stream (low-latency input + live frames). May be null.</summary>
        public StreamManager Stream { get; }

        public ToolContext(AdbClient adb, ServerConfig config, ScrcpyMirror mirror,
                           Safety.ShellPolicy shellPolicy, StreamManager stream = null)
        {
            Adb = adb;
            Config = config;
            Mirror = mirror;
            ShellPolicy = shellPolicy;
            Stream = stream;
        }

        /// <summary>Which transport input is currently routed over.</summary>
        public string InputTransport => (Stream?.Control != null) ? "scrcpy-control" : "adb-shell";

        // ---- input routing: prefer the held-open control socket, else adb shell -------

        public void Tap(int x, int y, bool longPress)
        {
            var ctl = Stream?.Control;
            if (ctl != null) { if (longPress) ctl.LongPress(x, y); else ctl.Tap(x, y); return; }
            RunShell(longPress ? "input swipe " + x + " " + y + " " + x + " " + y + " 800"
                               : "input tap " + x + " " + y);
        }

        public void Swipe(int x1, int y1, int x2, int y2, int durationMs)
        {
            var ctl = Stream?.Control;
            if (ctl != null) { ctl.Swipe(x1, y1, x2, y2, durationMs); return; }
            RunShell("input swipe " + x1 + " " + y1 + " " + x2 + " " + y2 + " " + durationMs);
        }

        public void TypeText(string text)
        {
            var ctl = Stream?.Control;
            if (ctl != null) { ctl.TypeText(text); return; }
            RunShell("input text " + InputEncoding.EscapeForInputText(text));
        }

        public void PressKey(int keycode)
        {
            var ctl = Stream?.Control;
            if (ctl != null) { ctl.PressKey(keycode); return; }
            RunShell("input keyevent " + keycode);
        }

        private void RunShell(string cmd)
        {
            var r = Adb.Shell(cmd);
            if (!r.Ok) throw new AdbException(r.DescribeFailure(cmd));
        }
    }

    /// <summary>
    /// The result of a tool call, shaped as MCP content blocks. Tool-level failures are
    /// carried as IsError=true results (not JSON-RPC errors), which is what MCP clients
    /// expect so the model can read and recover from the failure.
    /// </summary>
    public sealed class ToolResult
    {
        public List<JObject> Content { get; } = new List<JObject>();
        public bool IsError { get; set; }

        public static ToolResult Text(string text)
        {
            var r = new ToolResult();
            r.AddText(text);
            return r;
        }

        public static ToolResult Error(string message)
        {
            var r = new ToolResult { IsError = true };
            r.AddText(message);
            return r;
        }

        public ToolResult AddText(string text)
        {
            Content.Add(new JObject { ["type"] = "text", ["text"] = text ?? "" });
            return this;
        }

        public ToolResult AddImage(string base64Data, string mimeType)
        {
            Content.Add(new JObject
            {
                ["type"] = "image",
                ["data"] = base64Data,
                ["mimeType"] = mimeType,
            });
            return this;
        }

        public JObject ToResultObject()
        {
            var arr = new JArray();
            foreach (var c in Content) arr.Add(c);
            return new JObject { ["content"] = arr, ["isError"] = IsError };
        }
    }

    /// <summary>A callable MCP tool.</summary>
    public interface ITool
    {
        string Name { get; }
        string Description { get; }

        /// <summary>JSON Schema describing the tool's arguments (the MCP inputSchema).</summary>
        JObject InputSchema { get; }

        ToolResult Invoke(JObject arguments, ToolContext ctx);
    }

    /// <summary>Small helpers for reading tool arguments defensively.</summary>
    public static class Args
    {
        public static string Str(JObject a, string key, string fallback = null)
        {
            var t = a?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return t.Type == JTokenType.String ? t.Value<string>() : t.ToString();
        }

        public static bool Bool(JObject a, string key, bool fallback = false)
        {
            var t = a?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Boolean) return t.Value<bool>();
            if (t.Type == JTokenType.String) return string.Equals(t.Value<string>(), "true", StringComparison.OrdinalIgnoreCase);
            return fallback;
        }

        public static int? Int(JObject a, string key)
        {
            var t = a?[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return t.Value<int>();
            if (t.Type == JTokenType.Float) return (int)Math.Round(t.Value<double>());
            if (t.Type == JTokenType.String && int.TryParse(t.Value<string>(), out int v)) return v;
            return null;
        }

        public static int IntOr(JObject a, string key, int fallback) => Int(a, key) ?? fallback;

        public static bool Has(JObject a, string key)
        {
            var t = a?[key];
            return t != null && t.Type != JTokenType.Null;
        }
    }

    /// <summary>Convenience builders for JSON Schema fragments used in InputSchema.</summary>
    public static class Schema
    {
        public static JObject Object(JObject properties, params string[] required)
        {
            var o = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties ?? new JObject(),
            };
            if (required != null && required.Length > 0)
            {
                var arr = new JArray();
                foreach (var r in required) arr.Add(r);
                o["required"] = arr;
            }
            o["additionalProperties"] = false;
            return o;
        }

        public static JObject Prop(string type, string description)
            => new JObject { ["type"] = type, ["description"] = description };

        public static JObject StringProp(string description) => Prop("string", description);
        public static JObject IntProp(string description) => Prop("integer", description);
        public static JObject BoolProp(string description) => Prop("boolean", description);

        public static JObject EnumProp(string description, params string[] values)
        {
            var arr = new JArray();
            foreach (var v in values) arr.Add(v);
            return new JObject { ["type"] = "string", ["description"] = description, ["enum"] = arr };
        }
    }
}
