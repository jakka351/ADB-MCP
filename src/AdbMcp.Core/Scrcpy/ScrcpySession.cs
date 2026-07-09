using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using AdbMcp.Adb;
using AdbMcp.Logging;

namespace AdbMcp.Scrcpy
{
    /// <summary>Receives demuxed video packets from a running session (e.g. a decoder).</summary>
    public interface IVideoPacketSink
    {
        void OnCodecMeta(VideoCodecMeta meta);
        void OnPacket(VideoPacket packet);
        void OnClosed();
    }

    /// <summary>
    /// Orchestrates a programmatic scrcpy streaming session: pushes the server JAR,
    /// forwards a local TCP port to the device's abstract socket, launches the server
    /// via app_process, and connects the video + control sockets. The video stream is
    /// demuxed on a reader thread and pushed to a sink; the control socket carries
    /// low-latency input.
    ///
    /// End-to-end operation requires a device, a modern adb, and a scrcpy-server.jar
    /// matching ScrcpyOptions.ServerVersion. The protocol codecs are unit-tested; this
    /// orchestration is verified against a device.
    /// </summary>
    public sealed class ScrcpySession : IDisposable
    {
        private readonly AdbClient _adb;
        private readonly ScrcpyOptions _opt;
        private readonly IVideoPacketSink _sink;
        private readonly string _scrcpyExePath;

        private System.Diagnostics.Process _serverProc;
        private TcpClient _videoSock;
        private TcpClient _controlSock;
        private Thread _videoThread;
        private volatile bool _running;
        private bool _forwardAdded;

        public string DeviceName { get; private set; }
        public VideoCodecMeta CodecMeta { get; private set; }
        public ScrcpyControlClient Control { get; private set; }
        public bool IsRunning => _running;

        public ScrcpySession(AdbClient adb, ScrcpyOptions options, IVideoPacketSink sink, string scrcpyExePath = null)
        {
            _adb = adb ?? throw new ArgumentNullException(nameof(adb));
            _opt = options ?? new ScrcpyOptions();
            _sink = sink;
            _scrcpyExePath = scrcpyExePath;
        }

        public void Start()
        {
            if (_running) throw new InvalidOperationException("Session already running.");

            string jar = ResolveServerJar();
            PushServer(jar);
            AddForward();
            StartServerProcess();
            ConnectSockets();

            _running = true;
            _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "scrcpy-video" };
            _videoThread.Start();
            Log.Info("scrcpy session live: device=" + (DeviceName ?? "?") + " video=" +
                     (CodecMeta != null ? CodecMeta.CodecId + " " + CodecMeta.Width + "x" + CodecMeta.Height : "?") +
                     " control=" + (Control != null));
        }

        private string ResolveServerJar()
        {
            if (!string.IsNullOrEmpty(_opt.ServerJarPath) && File.Exists(_opt.ServerJarPath))
                return _opt.ServerJarPath;

            foreach (var candidate in ServerJarCandidates())
                if (File.Exists(candidate)) return candidate;

            throw new FileNotFoundException(
                "scrcpy-server JAR not found. Set scrcpyServerJar in config or place scrcpy-server next to scrcpy.exe. " +
                "It must match server version " + _opt.ServerVersion + ".");
        }

        private IEnumerable<string> ServerJarCandidates()
        {
            if (!string.IsNullOrEmpty(_scrcpyExePath))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(_scrcpyExePath)) ?? ".";
                yield return Path.Combine(dir, "scrcpy-server");
                yield return Path.Combine(dir, "scrcpy-server.jar");
            }
            // Common install layouts.
            yield return @"C:\scrcpy\scrcpy-server";
            yield return @"C:\Program Files\scrcpy\scrcpy-server";
        }

        private void PushServer(string jar)
        {
            var r = _adb.Run(new List<string> { "push", jar, _opt.RemoteJarPath });
            if (!r.Ok) throw new AdbException(r.DescribeFailure("push scrcpy-server"));
            Log.Debug("Pushed scrcpy-server to " + _opt.RemoteJarPath);
        }

        private void AddForward()
        {
            var r = _adb.Run(new List<string> { "forward", "tcp:" + _opt.LocalPort, "localabstract:" + _opt.SocketName });
            if (!r.Ok) throw new AdbException(r.DescribeFailure("adb forward"));
            _forwardAdded = true;
        }

        private void StartServerProcess()
        {
            var opts = new List<string>
            {
                "tunnel_forward=true",
                "video=true",
                "audio=false",
                "control=" + (_opt.Control ? "true" : "false"),
                "max_size=" + _opt.MaxSize,
                "video_bit_rate=" + _opt.VideoBitRate,
                "video_codec=" + _opt.VideoCodec,
                "send_device_meta=" + Lower(_opt.SendDeviceMeta),
                "send_frame_meta=" + Lower(_opt.SendFrameMeta),
                "send_codec_meta=" + Lower(_opt.SendCodecMeta),
                "send_dummy_byte=" + Lower(_opt.SendDummyByte),
                "raw_stream=false",
                "cleanup=true",
                "log_level=info",
            };

            string command = "CLASSPATH=" + _opt.RemoteJarPath +
                             " app_process / com.genymobile.scrcpy.Server " + _opt.ServerVersion +
                             " " + string.Join(" ", opts);

            _serverProc = _adb.StartRaw(new List<string> { "shell", command });
            Log.Debug("scrcpy server launched: " + command);
        }

        private void ConnectSockets()
        {
            _videoSock = ConnectWithRetry();
            _videoSock.NoDelay = true;
            var videoStream = _videoSock.GetStream();
            var demux = new ScrcpyVideoStream(videoStream, _opt.SendFrameMeta);

            // Forward-tunnel handshake: the server writes one dummy byte on the first socket.
            if (_opt.SendDummyByte)
            {
                int dummy = videoStream.ReadByte();
                if (dummy < 0) throw new AdbException("scrcpy server closed before the handshake dummy byte.");
            }

            // The control socket is the next connection accepted by the server.
            if (_opt.Control)
            {
                _controlSock = ConnectWithRetry();
                _controlSock.NoDelay = true;
            }

            if (_opt.SendDeviceMeta) DeviceName = demux.ReadDeviceName();
            if (_opt.SendCodecMeta) CodecMeta = demux.ReadCodecMeta();

            _reader = demux;

            var size = DeviceQueries.GetScreenSize(_adb);
            int w = size.Known ? size.Width : (CodecMeta?.Width ?? 1080);
            int h = size.Known ? size.Height : (CodecMeta?.Height ?? 2340);
            if (_opt.Control && _controlSock != null)
                Control = new ScrcpyControlClient(_controlSock.GetStream(), w, h);
        }

        private ScrcpyVideoStream _reader;

        private TcpClient ConnectWithRetry()
        {
            var deadline = DateTime.UtcNow.AddSeconds(_opt.ConnectTimeoutSeconds);
            Exception last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var c = new TcpClient();
                    c.Connect("127.0.0.1", _opt.LocalPort);
                    return c;
                }
                catch (SocketException ex)
                {
                    last = ex;
                    Thread.Sleep(150);
                }
            }
            throw new AdbException("Could not connect to the scrcpy server on 127.0.0.1:" + _opt.LocalPort +
                                   " within " + _opt.ConnectTimeoutSeconds + "s. " +
                                   (last != null ? last.Message : ""));
        }

        private void VideoLoop()
        {
            try
            {
                if (CodecMeta != null) _sink?.OnCodecMeta(CodecMeta);
                while (_running)
                {
                    var packet = _reader.ReadPacket();
                    if (packet == null) break;
                    _sink?.OnPacket(packet);
                }
            }
            catch (Exception ex)
            {
                if (_running) Log.Warn("scrcpy video loop ended: " + ex.Message);
            }
            finally
            {
                _running = false;
                _sink?.OnClosed();
            }
        }

        private static string Lower(bool b) => b ? "true" : "false";

        public void Dispose()
        {
            _running = false;
            try { Control?.Close(); } catch { }
            try { _videoSock?.Close(); } catch { }
            try { _controlSock?.Close(); } catch { }
            try { _videoThread?.Join(1500); } catch { }

            if (_serverProc != null)
            {
                try { if (!_serverProc.HasExited) _serverProc.Kill(); } catch { }
                try { _serverProc.Dispose(); } catch { }
                _serverProc = null;
            }

            if (_forwardAdded)
            {
                try { _adb.Run(new List<string> { "forward", "--remove", "tcp:" + _opt.LocalPort }); } catch { }
                _forwardAdded = false;
            }
            Log.Info("scrcpy session disposed.");
        }
    }
}
