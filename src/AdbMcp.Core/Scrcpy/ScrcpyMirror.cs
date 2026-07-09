using System;
using System.Collections.Generic;
using System.Diagnostics;
using AdbMcp.Adb;
using AdbMcp.Logging;

namespace AdbMcp.Scrcpy
{
    /// <summary>
    /// Manages an optional scrcpy child process that provides the continuous,
    /// human-watchable mirror window.
    ///
    /// Design note (per the feasibility report's hybrid strategy): the agent's
    /// primary perception is the cheap UI-hierarchy dump plus sampled screencap
    /// frames (see get_state / get_screenshot). scrcpy here supplies the live
    /// human-observable feed running alongside. Wiring the model's get_frame to
    /// scrcpy's own H.264 video+control socket — so frames and input share one
    /// connection — is the documented next step; the frame plane is deliberately
    /// pluggable so that upgrade drops in without touching the tool surface.
    /// </summary>
    public sealed class ScrcpyMirror
    {
        private readonly string _scrcpyPath;
        private readonly string _serial;
        private Process _proc;
        private readonly object _gate = new object();

        public ScrcpyMirror(string scrcpyPath, string serial)
        {
            _scrcpyPath = string.IsNullOrWhiteSpace(scrcpyPath) ? "scrcpy" : scrcpyPath;
            _serial = serial;
        }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    try { return _proc != null && !_proc.HasExited; }
                    catch { return false; }
                }
            }
        }

        /// <summary>Launch the mirror window. Returns a human-readable status string.</summary>
        public string Start(IEnumerable<string> extraArgs = null)
        {
            lock (_gate)
            {
                if (IsRunning) return "scrcpy mirror already running (pid " + _proc.Id + ").";

                var args = new List<string>();
                if (!string.IsNullOrEmpty(_serial)) { args.Add("-s"); args.Add(_serial); }
                args.Add("--window-title");
                args.Add("ADB-MCP Mirror");
                if (extraArgs != null) args.AddRange(extraArgs);

                var psi = new ProcessStartInfo
                {
                    FileName = _scrcpyPath,
                    Arguments = ArgQuoting.Join(args),
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };

                try
                {
                    _proc = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    _proc = null;
                    throw new InvalidOperationException(
                        "Could not start scrcpy ('" + _scrcpyPath + "'). Install scrcpy and ensure it is on PATH " +
                        "or set scrcpyPath in the config. Underlying error: " + ex.Message, ex);
                }

                Log.Info("scrcpy mirror started (pid " + _proc.Id + ").");
                return "scrcpy mirror started (pid " + _proc.Id + "). A live device window should now be visible.";
            }
        }

        public string Stop()
        {
            lock (_gate)
            {
                if (_proc == null) return "No scrcpy mirror is running.";
                try
                {
                    if (!_proc.HasExited)
                    {
                        _proc.Kill();
                        _proc.WaitForExit(3000);
                    }
                    return "scrcpy mirror stopped.";
                }
                catch (Exception ex)
                {
                    return "Attempted to stop scrcpy mirror: " + ex.Message;
                }
                finally
                {
                    try { _proc.Dispose(); } catch { }
                    _proc = null;
                }
            }
        }
    }
}
