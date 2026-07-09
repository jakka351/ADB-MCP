using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AdbMcp.App
{
    /// <summary>
    /// Thin wrapper over the platform-tools adb binary. Everything the mirror and the
    /// control surface need routes through here: raw screencap bytes for the live frame,
    /// and input injection (tap/swipe/text/keyevent) for acting on the device.
    /// </summary>
    public static class Adb
    {
        // The bundled C:\Windows\adb.exe is a broken 2011 build; use real platform-tools.
        private static readonly string[] CandidatePaths =
        {
            @"C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe",
            @"C:\Program Files\Android\android-sdk\platform-tools\adb.exe",
            @"C:\platform-tools\adb.exe",
        };

        private static string _adbPath;
        public static string Serial { get; set; } // e.g. 192.168.0.212:41135, or null for the only device

        // Port 5037 (adb default) is squatted on this machine by the traccar Java service,
        // which hangs every adb client. Run adb on its own dedicated server port instead.
        public static int ServerPort { get; set; } = ResolveServerPort();

        private static int ResolveServerPort()
        {
            var env = Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT");
            if (int.TryParse(env, out int p) && p > 0) return p;
            return 5860;
        }

        /// <summary>Ensure our dedicated adb server is running (idempotent).</summary>
        public static void StartServer() => RunText("start-server", 15000);

        public static string AdbPath
        {
            get
            {
                if (_adbPath != null) return _adbPath;
                foreach (var p in CandidatePaths)
                    if (File.Exists(p)) { _adbPath = p; return _adbPath; }
                _adbPath = "adb"; // fall back to PATH
                return _adbPath;
            }
            set { _adbPath = value; }
        }

        private static string TargetArgs => string.IsNullOrEmpty(Serial) ? "" : "-s " + Serial + " ";
        private static string GlobalArgs => "-P " + ServerPort + " " + TargetArgs;

        /// <summary>Run an adb command and return combined stdout (text).</summary>
        public static string RunText(string args, int timeoutMs = 15000)
        {
            using (var p = NewProcess(args))
            {
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                var sb = new StringBuilder();
                var err = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) err.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "ERROR: adb timed out"; }
                p.WaitForExit();
                var outText = sb.ToString();
                if (outText.Trim().Length == 0 && err.Length > 0) return err.ToString();
                return outText;
            }
        }

        /// <summary>Run an adb command and return raw stdout bytes (for screencap PNG).</summary>
        public static byte[] RunBytes(string args, int timeoutMs = 15000)
        {
            using (var p = NewProcess(args))
            {
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = false;
                p.Start();
                using (var ms = new MemoryStream())
                {
                    var stdout = p.StandardOutput.BaseStream;
                    var buf = new byte[65536];
                    int n;
                    // Read on this thread; adb closes stdout when done.
                    var sw = Stopwatch.StartNew();
                    while ((n = stdout.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (sw.ElapsedMilliseconds > timeoutMs) break;
                    }
                    p.WaitForExit(2000);
                    return ms.ToArray();
                }
            }
        }

        private static Process NewProcess(string args)
        {
            var p = new Process();
            p.StartInfo.FileName = AdbPath;
            p.StartInfo.Arguments = GlobalArgs + args;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.StandardOutputEncoding = null;
            p.StartInfo.EnvironmentVariables["ANDROID_ADB_SERVER_PORT"] = ServerPort.ToString();
            return p;
        }

        // ---- High-level helpers -------------------------------------------------

        public static string Connect(string hostPort) => RunText("connect " + hostPort);
        public static string Pair(string hostPort, string code) => RunText("pair " + hostPort + " " + code, 20000);
        public static string Devices() => RunText("devices -l");

        /// <summary>Grab a screenshot as PNG bytes via exec-out screencap (no temp file).</summary>
        public static byte[] Screencap() => RunBytes("exec-out screencap -p", 8000);

        public static void Tap(int x, int y) => RunText($"shell input tap {x} {y}", 5000);

        public static void Swipe(int x1, int y1, int x2, int y2, int durationMs = 200)
            => RunText($"shell input swipe {x1} {y1} {x2} {y2} {durationMs}", 5000);

        public static void KeyEvent(int keycode) => RunText($"shell input keyevent {keycode}", 5000);

        public static void InputText(string text)
        {
            // adb input text needs spaces as %s and escapes for shell-special chars.
            var escaped = text
                .Replace("\\", "\\\\")
                .Replace(" ", "%s")
                .Replace("'", "\\'")
                .Replace("\"", "\\\"")
                .Replace("&", "\\&")
                .Replace("<", "\\<")
                .Replace(">", "\\>")
                .Replace("(", "\\(")
                .Replace(")", "\\)")
                .Replace("|", "\\|")
                .Replace(";", "\\;");
            RunText("shell input text \"" + escaped + "\"", 5000);
        }

        public static string GetWmSize() => RunText("shell wm size", 5000);
        public static string UiDump()
        {
            // Dump the accessibility hierarchy to stdout.
            return RunText("exec-out uiautomator dump /dev/tty", 8000);
        }

        // Common Android keycodes.
        public const int KEYCODE_BACK = 4;
        public const int KEYCODE_HOME = 3;
        public const int KEYCODE_APP_SWITCH = 187;
        public const int KEYCODE_POWER = 26;
        public const int KEYCODE_VOLUME_UP = 24;
        public const int KEYCODE_VOLUME_DOWN = 25;
        public const int KEYCODE_ENTER = 66;
        public const int KEYCODE_DEL = 67;
        public const int KEYCODE_MENU = 82;
        public const int KEYCODE_WAKEUP = 224;
    }
}
