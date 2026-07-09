using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AdbMcp.Config
{
    /// <summary>
    /// Runtime configuration for the ADB-MCP server. Values are resolved in this
    /// order (later wins): built-in defaults -> config file -> environment
    /// variables -> command-line flags.
    /// </summary>
    public sealed class ServerConfig
    {
        /// <summary>Path to the adb executable. "adb" resolves via PATH.</summary>
        [JsonProperty("adbPath")]
        public string AdbPath { get; set; } = "adb";

        /// <summary>Path to the scrcpy executable used for the human-watchable mirror.</summary>
        [JsonProperty("scrcpyPath")]
        public string ScrcpyPath { get; set; } = "scrcpy";

        /// <summary>Target device serial. Null/empty = let adb pick the single connected device.</summary>
        [JsonProperty("deviceSerial")]
        public string DeviceSerial { get; set; }

        /// <summary>Default per-command timeout in milliseconds.</summary>
        [JsonProperty("commandTimeoutMs")]
        public int CommandTimeoutMs { get; set; } = 15000;

        /// <summary>Longest edge (px) a sampled frame is downscaled to before encoding. 0 = no resize.</summary>
        [JsonProperty("maxFrameDimension")]
        public int MaxFrameDimension { get; set; } = 1080;

        /// <summary>JPEG quality (1-100) for sampled frames.</summary>
        [JsonProperty("jpegQuality")]
        public int JpegQuality { get; set; } = 65;

        /// <summary>Default frame encoding: "jpeg" (token-cheap) or "png" (lossless).</summary>
        [JsonProperty("frameFormat")]
        public string FrameFormat { get; set; } = "jpeg";

        /// <summary>
        /// When true, irreversible/costly tools (send_sms, calls) refuse to act unless
        /// the caller passes confirm=true. This is the agent authority gate.
        /// </summary>
        [JsonProperty("requireConfirmation")]
        public bool RequireConfirmation { get; set; } = true;

        /// <summary>
        /// Regex allowlist for the raw shell tool. A command is permitted only if it
        /// matches one of these (anchored, case-insensitive). Treat shell as a loaded
        /// weapon: keep this tight.
        /// </summary>
        [JsonProperty("shellAllowlist")]
        public List<string> ShellAllowlist { get; set; } = new List<string>(DefaultAllowlist);

        /// <summary>
        /// If true, shell commands that fail the allowlist may still run when the caller
        /// passes confirm=true. Off by default — the safe posture.
        /// </summary>
        [JsonProperty("allowUnlistedShellWithConfirm")]
        public bool AllowUnlistedShellWithConfirm { get; set; } = false;

        /// <summary>Diagnostic verbosity: debug|info|warn|error.</summary>
        [JsonProperty("logLevel")]
        public string LogLevel { get; set; } = "info";

        // ---- scrcpy streaming (low-latency video + control socket) --------------------

        /// <summary>Path to scrcpy-server (the JAR). Auto-detected next to scrcpy.exe if null.</summary>
        [JsonProperty("scrcpyServerJar")]
        public string ScrcpyServerJar { get; set; }

        /// <summary>scrcpy server protocol version; MUST match the scrcpy-server JAR.</summary>
        [JsonProperty("scrcpyServerVersion")]
        public string ScrcpyServerVersion { get; set; } = "2.4";

        /// <summary>Longest edge (px) of the streamed video (server-side downscale).</summary>
        [JsonProperty("streamMaxSize")]
        public int StreamMaxSize { get; set; } = 1024;

        /// <summary>
        /// Attempt to decode streamed H.264 frames via Media Foundation so the model can
        /// sample the live feed. If false (or if decode is unavailable), frames come from
        /// screencap while input still uses the low-latency control channel.
        /// </summary>
        [JsonProperty("enableStreamDecode")]
        public bool EnableStreamDecode { get; set; } = true;

        /// <summary>
        /// Conservative default allowlist: read-only inspection and the standard input
        /// actuators. No destructive filesystem, package, or settings mutation.
        /// </summary>
        public static readonly string[] DefaultAllowlist =
        {
            @"^input(\s+.*)?$",
            @"^wm\s+size$",
            @"^wm\s+density$",
            @"^dumpsys\s+.*$",
            @"^getprop(\s+.*)?$",
            @"^pm\s+list\s+.*$",
            @"^settings\s+get\s+.*$",
            @"^screencap(\s+.*)?$",
            @"^uiautomator\s+dump.*$",
            @"^am\s+start(\s+.*)?$",
            @"^monkey\s+.*$",
            @"^cat\s+/sdcard/.*$",
            @"^ls(\s+.*)?$",
            @"^echo(\s+.*)?$"
        };

        public static ServerConfig LoadFromFile(string path)
        {
            var json = File.ReadAllText(path);
            var cfg = JsonConvert.DeserializeObject<ServerConfig>(json) ?? new ServerConfig();
            return cfg;
        }

        /// <summary>Overlay ADB_MCP_* environment variables onto this config.</summary>
        public void ApplyEnvironment()
        {
            string v;
            if (!string.IsNullOrEmpty(v = Environment.GetEnvironmentVariable("ADB_MCP_ADB"))) AdbPath = v;
            if (!string.IsNullOrEmpty(v = Environment.GetEnvironmentVariable("ADB_MCP_SCRCPY"))) ScrcpyPath = v;
            if (!string.IsNullOrEmpty(v = Environment.GetEnvironmentVariable("ADB_MCP_DEVICE"))) DeviceSerial = v;
            if (!string.IsNullOrEmpty(v = Environment.GetEnvironmentVariable("ADB_MCP_LOG"))) LogLevel = v;
        }

        public void Validate()
        {
            if (JpegQuality < 1) JpegQuality = 1;
            if (JpegQuality > 100) JpegQuality = 100;
            if (CommandTimeoutMs < 1000) CommandTimeoutMs = 1000;
            if (MaxFrameDimension < 0) MaxFrameDimension = 0;
            if (string.IsNullOrWhiteSpace(FrameFormat)) FrameFormat = "jpeg";
            FrameFormat = FrameFormat.ToLowerInvariant();
            if (FrameFormat != "jpeg" && FrameFormat != "png") FrameFormat = "jpeg";
            if (ShellAllowlist == null) ShellAllowlist = new List<string>(DefaultAllowlist);
        }
    }
}
