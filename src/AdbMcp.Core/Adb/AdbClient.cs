using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using AdbMcp.Logging;

namespace AdbMcp.Adb
{
    /// <summary>
    /// Thin, binary-safe wrapper over the adb executable. All device I/O in the
    /// server funnels through here. Reads stdout as raw bytes (so screencap PNGs
    /// survive), reads stderr on a side thread to avoid pipe deadlocks, and
    /// enforces a hard timeout by killing the process.
    /// </summary>
    public sealed class AdbClient
    {
        private readonly string _adbPath;
        private readonly int _defaultTimeoutMs;

        /// <summary>Selected device serial, or null to let adb choose the single device.</summary>
        public string Serial { get; private set; }

        public AdbClient(string adbPath, string serial, int defaultTimeoutMs)
        {
            _adbPath = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath;
            Serial = string.IsNullOrWhiteSpace(serial) ? null : serial.Trim();
            _defaultTimeoutMs = defaultTimeoutMs > 0 ? defaultTimeoutMs : 15000;
        }

        public void SetSerial(string serial) => Serial = string.IsNullOrWhiteSpace(serial) ? null : serial.Trim();

        /// <summary>Run an adb command. Prepends "-s SERIAL" when a serial is set and includeSerial is true.</summary>
        public ProcessResult Run(IList<string> args, int? timeoutMs = null, bool includeSerial = true)
        {
            var full = new List<string>();
            if (includeSerial && !string.IsNullOrEmpty(Serial)) { full.Add("-s"); full.Add(Serial); }
            full.AddRange(args);

            string argLine = ArgQuoting.Join(full);
            Log.Debug("adb " + argLine);

            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = argLine,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new AdbException("Could not start adb ('" + _adbPath + "'). Is it installed and on PATH? " + ex.Message, ex);
            }

            using (p)
            {
                var stdout = new MemoryStream();
                var stderr = new MemoryStream();

                var tOut = new Thread(() => { try { p.StandardOutput.BaseStream.CopyTo(stdout); } catch { } }) { IsBackground = true };
                var tErr = new Thread(() => { try { p.StandardError.BaseStream.CopyTo(stderr); } catch { } }) { IsBackground = true };
                tOut.Start();
                tErr.Start();

                int timeout = timeoutMs ?? _defaultTimeoutMs;
                if (!p.WaitForExit(timeout))
                {
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(2000); } catch { }
                    tOut.Join(1000);
                    tErr.Join(1000);
                    throw new AdbException("adb command timed out after " + timeout + " ms: adb " + argLine);
                }

                // Process exited: let the reader threads drain the pipes.
                tOut.Join(3000);
                tErr.Join(3000);

                return new ProcessResult(p.ExitCode, stdout.ToArray(), Encoding.UTF8.GetString(stderr.ToArray()));
            }
        }

        public ProcessResult Run(params string[] args) => Run(args, null, true);

        /// <summary>
        /// Start an adb command as a long-lived background process and return the handle
        /// without waiting. Used for the scrcpy server (app_process), which stays running
        /// for the life of the stream. stdout/stderr are drained to the log so the pipe
        /// never blocks.
        /// </summary>
        public Process StartRaw(IList<string> args, bool includeSerial = true)
        {
            var full = new List<string>();
            if (includeSerial && !string.IsNullOrEmpty(Serial)) { full.Add("-s"); full.Add(Serial); }
            full.AddRange(args);

            string argLine = ArgQuoting.Join(full);
            Log.Debug("adb (bg) " + argLine);

            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = argLine,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            Process p;
            try { p = Process.Start(psi); }
            catch (Exception ex)
            {
                throw new AdbException("Could not start adb ('" + _adbPath + "'): " + ex.Message, ex);
            }

            new Thread(() => DrainToLog(p.StandardOutput, "server-out")) { IsBackground = true }.Start();
            new Thread(() => DrainToLog(p.StandardError, "server-err")) { IsBackground = true }.Start();
            return p;
        }

        private static void DrainToLog(System.IO.StreamReader reader, string tag)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    Log.Debug("[scrcpy-" + tag + "] " + line);
            }
            catch { /* pipe closed */ }
        }

        /// <summary>Run <c>adb shell &lt;command&gt;</c>. The command is passed as one token to adb,
        /// which forwards it to the device shell.</summary>
        public ProcessResult Shell(string command, int? timeoutMs = null)
            => Run(new List<string> { "shell", command }, timeoutMs, includeSerial: true);

        public string ShellText(string command, int? timeoutMs = null)
        {
            var r = Shell(command, timeoutMs);
            if (!r.Ok) throw new AdbException(r.DescribeFailure("adb shell " + command));
            return r.StdOutText;
        }

        /// <summary>List attached devices via <c>adb devices -l</c>.</summary>
        public List<DeviceInfo> ListDevices()
        {
            var r = Run(new List<string> { "devices", "-l" }, null, includeSerial: false);
            var list = new List<DeviceInfo>();
            if (!r.Ok) return list;

            foreach (var raw in r.StdOutText.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var info = new DeviceInfo { Serial = parts[0], State = parts[1] };
                for (int i = 2; i < parts.Length; i++)
                {
                    var kv = parts[i];
                    int eq = kv.IndexOf(':');
                    if (eq <= 0) continue;
                    string key = kv.Substring(0, eq), val = kv.Substring(eq + 1);
                    switch (key)
                    {
                        case "model": info.Model = val; break;
                        case "product": info.Product = val; break;
                        case "transport_id": info.TransportId = val; break;
                    }
                }
                list.Add(info);
            }
            return list;
        }

        /// <summary>
        /// Capture the current screen as PNG bytes. Prefers <c>exec-out screencap -p</c>
        /// (clean binary). Falls back to screencap-to-file + pull for older adb builds
        /// that lack exec-out or mangle binary over the shell channel.
        /// </summary>
        public byte[] Screencap()
        {
            try
            {
                var r = Run(new List<string> { "exec-out", "screencap", "-p" });
                if (r.Ok && LooksLikePng(r.StdOut))
                    return r.StdOut;
                Log.Debug("exec-out screencap unusable (exit " + r.ExitCode + ", " + r.StdOut.Length + " bytes); using file fallback.");
            }
            catch (AdbException ex)
            {
                Log.Debug("exec-out screencap failed, using file fallback: " + ex.Message);
            }

            const string remote = "/sdcard/_adbmcp_frame.png";
            var cap = Shell("screencap -p " + remote);
            if (!cap.Ok)
                throw new AdbException(cap.DescribeFailure("screencap"));

            string local = Path.Combine(Path.GetTempPath(), "adbmcp_frame_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var pull = Run(new List<string> { "pull", remote, local });
                if (!pull.Ok || !File.Exists(local))
                    throw new AdbException(pull.DescribeFailure("adb pull screencap"));
                var bytes = File.ReadAllBytes(local);
                if (!LooksLikePng(bytes))
                    throw new AdbException("Pulled screencap is not a valid PNG (" + bytes.Length + " bytes).");
                return bytes;
            }
            finally
            {
                try { if (File.Exists(local)) File.Delete(local); } catch { }
                try { Shell("rm -f " + remote); } catch { }
            }
        }

        public static bool LooksLikePng(byte[] b)
        {
            if (b == null || b.Length < 8) return false;
            return b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
                && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
        }
    }
}
