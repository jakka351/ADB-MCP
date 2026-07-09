namespace AdbMcp.Scrcpy
{
    /// <summary>
    /// Parameters for a programmatic scrcpy streaming session. Defaults target the
    /// scrcpy 2.x server protocol. The scrcpy-server.jar pushed to the device MUST
    /// match ServerVersion, or the handshake will fail.
    /// </summary>
    public sealed class ScrcpyOptions
    {
        /// <summary>Local filesystem path to scrcpy-server (the JAR). Auto-detected next to scrcpy.exe if null.</summary>
        public string ServerJarPath { get; set; }

        /// <summary>scrcpy server version string passed as the first app_process arg (must match the JAR).</summary>
        public string ServerVersion { get; set; } = "2.4";

        /// <summary>Remote path the JAR is pushed to on the device.</summary>
        public string RemoteJarPath { get; set; } = "/data/local/tmp/scrcpy-server-manual.jar";

        /// <summary>Abstract socket name the server listens on (localabstract:&lt;name&gt;).</summary>
        public string SocketName { get; set; } = "scrcpy";

        /// <summary>Local TCP port to forward to the device abstract socket.</summary>
        public int LocalPort { get; set; } = 27183;

        /// <summary>Cap the streamed video's longest edge (server-side downscale). 0 = device resolution.</summary>
        public int MaxSize { get; set; } = 1024;

        /// <summary>Requested video bit rate (bits/sec).</summary>
        public int VideoBitRate { get; set; } = 8_000_000;

        /// <summary>Video codec: h264 is the most broadly decodable.</summary>
        public string VideoCodec { get; set; } = "h264";

        /// <summary>Open the control channel for low-latency input injection.</summary>
        public bool Control { get; set; } = true;

        /// <summary>Server sends a 64-byte device name at stream start (forward-tunnel default).</summary>
        public bool SendDeviceMeta { get; set; } = true;

        /// <summary>Server sends 12-byte codec metadata after the device name.</summary>
        public bool SendCodecMeta { get; set; } = true;

        /// <summary>Server prefixes each frame with a 12-byte PTS/flags/length header.</summary>
        public bool SendFrameMeta { get; set; } = true;

        /// <summary>Server sends a single dummy byte on the first socket (forward-tunnel handshake).</summary>
        public bool SendDummyByte { get; set; } = true;

        /// <summary>Seconds to wait for the server to come up and the sockets to connect.</summary>
        public int ConnectTimeoutSeconds { get; set; } = 10;
    }
}
