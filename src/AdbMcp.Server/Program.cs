using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdbMcp.Adb;
using AdbMcp.Config;
using AdbMcp.Logging;
using AdbMcp.Mcp;
using AdbMcp.Safety;
using AdbMcp.Scrcpy;
using Newtonsoft.Json;

namespace AdbMcp.Server
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var cli = ParseArgs(args);

            if (cli.ContainsKey("help") || cli.ContainsKey("h"))
            {
                PrintHelp();
                return 0;
            }

            // Resolve configuration: defaults -> file -> environment -> CLI flags.
            ServerConfig config;
            try
            {
                config = cli.TryGetValue("config", out var cfgPath) && !string.IsNullOrEmpty(cfgPath)
                    ? ServerConfig.LoadFromFile(cfgPath)
                    : new ServerConfig();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to load config: " + ex.Message);
                return 2;
            }

            config.ApplyEnvironment();
            ApplyCliOverrides(config, cli);
            config.Validate();

            Log.Minimum = ParseLevel(config.LogLevel);

            // ADB client + device selection.
            var adb = new AdbClient(config.AdbPath, config.DeviceSerial, config.CommandTimeoutMs);
            if (string.IsNullOrEmpty(adb.Serial))
                AutoSelectDevice(adb);

            // Diagnostic modes that don't start the MCP loop.
            if (cli.ContainsKey("list-tools"))
            {
                var reg = DefaultTools.BuildRegistry();
                Console.Out.WriteLine(reg.ToListJson().ToString(Formatting.Indented));
                return 0;
            }
            if (cli.ContainsKey("self-check"))
                return SelfCheck(adb, config);
            if (cli.ContainsKey("test-decoder"))
                return TestDecoder();

            // Build the tool context and server.
            var mirror = new ScrcpyMirror(config.ScrcpyPath, adb.Serial);
            var policy = new ShellPolicy(config.ShellAllowlist, config.AllowUnlistedShellWithConfirm);
            var stream = new StreamManager(adb, config);
            var context = new ToolContext(adb, config, mirror, policy, stream);
            var registry = DefaultTools.BuildRegistry();

            // Wire raw stdio as UTF-8 (no BOM). stdout carries the JSON-RPC stream;
            // all diagnostics go to stderr via Log.
            var utf8 = new UTF8Encoding(false);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
            var stdin = new StreamReader(Console.OpenStandardInput(), utf8);

            Log.Info("adb path: " + config.AdbPath + " | device: " + (adb.Serial ?? "(auto)") +
                     " | confirmations: " + (config.RequireConfirmation ? "on" : "off"));

            var server = new McpServer(registry, context, stdin, stdout);
            try
            {
                server.Run();
            }
            catch (Exception ex)
            {
                Log.Error("Fatal server error", ex);
                return 1;
            }
            finally
            {
                try { stream.Dispose(); } catch { }
                try { mirror.Stop(); } catch { }
                try { stdout.Flush(); } catch { }
            }
            return 0;
        }

        private static void AutoSelectDevice(AdbClient adb)
        {
            List<DeviceInfo> devices;
            try { devices = adb.ListDevices(); }
            catch (Exception ex) { Log.Warn("Could not enumerate devices: " + ex.Message); return; }

            var usable = devices.FindAll(d => d.IsUsable);
            if (usable.Count == 1)
            {
                adb.SetSerial(usable[0].Serial);
                Log.Info("Auto-selected device: " + usable[0].Serial);
            }
            else if (usable.Count == 0)
            {
                Log.Warn("No usable device is connected yet. Tools will error until one appears " +
                         "(check 'adb devices', authorize USB debugging, or 'adb connect <ip>').");
            }
            else
            {
                adb.SetSerial(usable[0].Serial);
                Log.Warn(usable.Count + " devices connected; defaulting to " + usable[0].Serial +
                         ". Set deviceSerial / --device to choose explicitly.");
            }
        }

        private static int SelfCheck(AdbClient adb, ServerConfig config)
        {
            Console.Error.WriteLine("== ADB-MCP self-check ==");
            Console.Error.WriteLine("adb path : " + config.AdbPath);
            try
            {
                var version = adb.Run(new List<string> { "version" }, null, includeSerial: false);
                Console.Error.WriteLine(version.StdOutText);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("adb not runnable: " + ex.Message);
                return 2;
            }

            try
            {
                var devices = adb.ListDevices();
                Console.Error.WriteLine("devices  : " + devices.Count);
                foreach (var d in devices) Console.Error.WriteLine("  " + d);
                Console.Error.WriteLine("target   : " + (adb.Serial ?? "(none)"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("device enumeration failed: " + ex.Message);
            }
            Console.Error.WriteLine("tools    : " + DefaultTools.BuildRegistry().Count + " registered");
            Console.Error.WriteLine("OK");
            return 0;
        }

        private static int TestDecoder()
        {
            Console.Error.WriteLine("== Media Foundation H.264 decoder probe (no device needed) ==");
            try
            {
                using (var dec = new AdbMcp.Mf.MediaFoundationH264Decoder())
                {
                    bool ok = dec.Initialize(1280, 720);
                    Console.Error.WriteLine("Initialize(1280x720): " + (ok ? "OK — MFT created, input/output types negotiated" : "unavailable"));
                    Console.Error.WriteLine("IsAvailable: " + dec.IsAvailable);
                    Console.Error.WriteLine(ok
                        ? "The COM interop path is valid on this machine. Full-frame decode is verified against a live stream."
                        : "Decoder could not initialise; streaming will fall back to screencap for frames.");
                    return ok ? 0 : 3;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Decoder probe threw: " + ex.Message);
                return 3;
            }
        }

        // ---- CLI plumbing -------------------------------------------------------------

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("-")) continue;
                string key = a.TrimStart('-');

                // "--key=value" form
                int eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    map[key.Substring(0, eq)] = key.Substring(eq + 1);
                    continue;
                }

                // "--flag" (boolean) or "--key value"
                bool nextIsValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
                if (nextIsValue && !IsKnownFlag(key))
                {
                    map[key] = args[++i];
                }
                else
                {
                    map[key] = "true";
                }
            }
            return map;
        }

        private static bool IsKnownFlag(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "help": case "h":
                case "self-check": case "list-tools": case "test-decoder":
                case "auto-confirm": case "allow-unlisted-shell":
                    return true;
                default:
                    return false;
            }
        }

        private static void ApplyCliOverrides(ServerConfig config, Dictionary<string, string> cli)
        {
            if (cli.TryGetValue("adb", out var adb) && !string.IsNullOrEmpty(adb)) config.AdbPath = adb;
            if (cli.TryGetValue("scrcpy", out var sc) && !string.IsNullOrEmpty(sc)) config.ScrcpyPath = sc;
            if (cli.TryGetValue("device", out var dev) && !string.IsNullOrEmpty(dev)) config.DeviceSerial = dev;
            if (cli.TryGetValue("log-level", out var ll) && !string.IsNullOrEmpty(ll)) config.LogLevel = ll;
            if (cli.TryGetValue("max-frame-dim", out var mfd) && int.TryParse(mfd, out var mfdv)) config.MaxFrameDimension = mfdv;
            if (cli.TryGetValue("jpeg-quality", out var jq) && int.TryParse(jq, out var jqv)) config.JpegQuality = jqv;
            if (cli.ContainsKey("auto-confirm")) config.RequireConfirmation = false;
            if (cli.ContainsKey("allow-unlisted-shell")) config.AllowUnlistedShellWithConfirm = true;
        }

        private static Log.Level ParseLevel(string level)
        {
            switch ((level ?? "info").ToLowerInvariant())
            {
                case "debug": return Log.Level.Debug;
                case "warn": case "warning": return Log.Level.Warn;
                case "error": return Log.Level.Error;
                default: return Log.Level.Info;
            }
        }

        private static void PrintHelp()
        {
            Console.Error.WriteLine(
@"adb-mcp-server — AI-controlled Android over ADB, exposed via the Model Context Protocol.

USAGE:
  adb-mcp-server [options]           Start the MCP server (JSON-RPC 2.0 over stdio).

OPTIONS:
  --config <file>            Load settings from a JSON config file.
  --adb <path>              Path to adb executable (default: adb on PATH).
  --scrcpy <path>           Path to scrcpy executable (default: scrcpy on PATH).
  --device <serial>         Target device serial (default: auto-select single device).
  --log-level <lvl>         debug | info | warn | error (default: info).
  --max-frame-dim <px>      Downscale sampled frames to this longest edge.
  --jpeg-quality <1-100>    JPEG quality for sampled frames.
  --auto-confirm            Disable confirmation gates (DANGEROUS; test devices only).
  --allow-unlisted-shell    Permit non-allowlisted shell commands with confirm=true.

DIAGNOSTICS (do not start the server):
  --self-check              Verify adb, list devices, count tools, then exit.
  --list-tools              Print the tool catalog (names + JSON schemas) and exit.
  --help                    Show this help.

Logs go to stderr; stdout is reserved for the JSON-RPC message stream.");
        }
    }
}
