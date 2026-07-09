using System;
using System.Drawing;
using System.Text;
using AdbMcp.Adb;
using AdbMcp.Config;
using AdbMcp.Imaging;
using AdbMcp.Logging;
using AdbMcp.Mf;

namespace AdbMcp.Scrcpy
{
    /// <summary>
    /// Owns the optional scrcpy streaming session and exposes it to the tool layer:
    /// the low-latency control channel for input and the live decoded frame for
    /// perception. Everything degrades gracefully — no session means input falls back
    /// to <c>adb shell input</c> and frames to screencap.
    /// </summary>
    public sealed class StreamManager : IDisposable
    {
        private readonly AdbClient _adb;
        private readonly ServerConfig _cfg;
        private readonly object _gate = new object();

        private ScrcpySession _session;
        private ScrcpyFrameSource _frames;

        public StreamManager(AdbClient adb, ServerConfig cfg)
        {
            _adb = adb;
            _cfg = cfg;
        }

        public bool IsStreaming
        {
            get { lock (_gate) return _session != null && _session.IsRunning; }
        }

        /// <summary>The live control channel, or null when not streaming.</summary>
        public ScrcpyControlClient Control
        {
            get { lock (_gate) return _session?.Control; }
        }

        /// <summary>True when a decoded live frame is available to sample.</summary>
        public bool HasLiveFrame
        {
            get { lock (_gate) return _frames != null && _frames.HasFrame; }
        }

        public Bitmap CaptureLiveFrame()
        {
            lock (_gate) return _frames?.CaptureLatest();
        }

        public string Start(int? maxSize)
        {
            lock (_gate)
            {
                if (IsStreaming) return "A scrcpy stream is already running. " + StatusUnlocked();

                var opt = new ScrcpyOptions
                {
                    ServerJarPath = _cfg.ScrcpyServerJar,
                    ServerVersion = _cfg.ScrcpyServerVersion,
                    MaxSize = maxSize ?? _cfg.StreamMaxSize,
                    Control = true,
                };

                IVideoDecoder decoder = CreateDecoder();
                _frames = new ScrcpyFrameSource(decoder);
                _session = new ScrcpySession(_adb, opt, _frames, _cfg.ScrcpyPath);

                try
                {
                    _session.Start();
                }
                catch (Exception)
                {
                    SafeTeardown();
                    throw;
                }

                var sb = new StringBuilder();
                sb.Append("scrcpy stream started. device=").Append(_session.DeviceName ?? "?");
                if (_session.CodecMeta != null)
                    sb.Append(" video=").Append(_session.CodecMeta.CodecId).Append(' ')
                      .Append(_session.CodecMeta.Width).Append('x').Append(_session.CodecMeta.Height);
                sb.Append(" control=").Append(_session.Control != null ? "on (low-latency input)" : "off");
                sb.Append(" decode=").Append(_frames.DecoderAvailable ? "on (live frames)" : "off (frames via screencap)");
                Log.Info(sb.ToString());
                return sb.ToString();
            }
        }

        private IVideoDecoder CreateDecoder()
        {
            if (!_cfg.EnableStreamDecode) return new NullVideoDecoder();
            try { return new MediaFoundationH264Decoder(); }
            catch (Exception ex)
            {
                Log.Warn("Could not create Media Foundation decoder: " + ex.Message);
                return new NullVideoDecoder();
            }
        }

        public string Stop()
        {
            lock (_gate)
            {
                if (_session == null) return "No scrcpy stream is running.";
                SafeTeardown();
                return "scrcpy stream stopped.";
            }
        }

        public string Status()
        {
            lock (_gate) return StatusUnlocked();
        }

        private string StatusUnlocked()
        {
            if (_session == null || !_session.IsRunning) return "scrcpy stream: not running.";
            var sb = new StringBuilder("scrcpy stream: running");
            sb.Append(" | input=").Append(_session.Control != null ? "control-channel" : "adb-shell");
            if (_frames != null)
            {
                sb.Append(" | decode=").Append(_frames.DecoderAvailable ? "on" : "off");
                sb.Append(" | frames=").Append(_frames.FramesDecoded).Append('/').Append(_frames.FramesReceived);
            }
            return sb.ToString();
        }

        private void SafeTeardown()
        {
            try { _session?.Dispose(); } catch { }
            try { _frames?.Dispose(); } catch { }
            _session = null;
            _frames = null;
        }

        public void Dispose() => Stop();
    }
}
