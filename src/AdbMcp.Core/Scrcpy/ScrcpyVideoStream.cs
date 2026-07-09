using System;
using System.IO;
using System.Text;

namespace AdbMcp.Scrcpy
{
    /// <summary>One demuxed unit from the scrcpy video socket.</summary>
    public sealed class VideoPacket
    {
        /// <summary>Presentation timestamp in microseconds (0 for config packets).</summary>
        public long PtsUs { get; set; }
        /// <summary>True for codec configuration packets (SPS/PPS) — feed to the decoder, don't display.</summary>
        public bool IsConfig { get; set; }
        /// <summary>True when this access unit is a key frame (IDR).</summary>
        public bool IsKeyFrame { get; set; }
        /// <summary>The raw Annex-B encoded payload.</summary>
        public byte[] Data { get; set; }
    }

    /// <summary>Codec/size metadata sent once at the start of the video stream.</summary>
    public sealed class VideoCodecMeta
    {
        public string CodecId { get; set; } // "h264", "h265", "av1"
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// Reads and demuxes the scrcpy 2.x video socket: a 64-byte device name, optional
    /// codec metadata (codec id + dimensions), then a sequence of frame packets each
    /// prefixed with a 12-byte header (8-byte PTS/flags + 4-byte length).
    /// </summary>
    public sealed class ScrcpyVideoStream
    {
        // Top two bits of the 64-bit PTS field are flags.
        private const ulong FLAG_CONFIG = 1UL << 63;
        private const ulong FLAG_KEY_FRAME = 1UL << 62;
        private const ulong PTS_MASK = (1UL << 62) - 1;

        private readonly Stream _stream;
        private readonly bool _hasFrameMeta;

        public ScrcpyVideoStream(Stream stream, bool hasFrameMeta = true)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _hasFrameMeta = hasFrameMeta;
        }

        /// <summary>Read the 64-byte device name sent when send_device_meta is enabled.</summary>
        public string ReadDeviceName()
        {
            var buf = ReadExact(64);
            int len = 0;
            while (len < buf.Length && buf[len] != 0) len++;
            return Encoding.UTF8.GetString(buf, 0, len);
        }

        /// <summary>Read the 12-byte codec metadata (codec id, width, height).</summary>
        public VideoCodecMeta ReadCodecMeta()
        {
            var buf = ReadExact(12);
            uint codec = BeReader.ReadU32(buf, 0);
            int width = (int)BeReader.ReadU32(buf, 4);
            int height = (int)BeReader.ReadU32(buf, 8);
            return new VideoCodecMeta { CodecId = CodecIdToString(codec), Width = width, Height = height };
        }

        /// <summary>
        /// Read one packet. Returns null at end of stream. When frame metadata is
        /// disabled, the entire remaining chunk is returned as a single non-config packet.
        /// </summary>
        public VideoPacket ReadPacket()
        {
            if (!_hasFrameMeta)
            {
                var chunk = ReadSome(65536);
                return chunk == null ? null : new VideoPacket { Data = chunk, IsKeyFrame = false, IsConfig = false };
            }

            var header = TryReadExact(12);
            if (header == null) return null;

            ulong ptsAndFlags = (ulong)BeReader.ReadI64(header, 0);
            uint size = BeReader.ReadU32(header, 8);

            bool isConfig = (ptsAndFlags & FLAG_CONFIG) != 0;
            bool isKey = (ptsAndFlags & FLAG_KEY_FRAME) != 0;
            long pts = isConfig ? 0 : (long)(ptsAndFlags & PTS_MASK);

            var data = ReadExact((int)size);
            return new VideoPacket { PtsUs = pts, IsConfig = isConfig, IsKeyFrame = isKey, Data = data };
        }

        private static string CodecIdToString(uint codec)
        {
            var chars = new char[4];
            chars[0] = (char)((codec >> 24) & 0xFF);
            chars[1] = (char)((codec >> 16) & 0xFF);
            chars[2] = (char)((codec >> 8) & 0xFF);
            chars[3] = (char)(codec & 0xFF);
            return new string(chars).Trim('\0', ' ');
        }

        // ---- low-level reads ----------------------------------------------------------

        private byte[] ReadExact(int n)
        {
            var b = TryReadExact(n);
            if (b == null) throw new EndOfStreamException("scrcpy video stream ended after reading " + n + " expected bytes.");
            return b;
        }

        private byte[] TryReadExact(int n)
        {
            var buf = new byte[n];
            int read = 0;
            while (read < n)
            {
                int r = _stream.Read(buf, read, n - read);
                if (r <= 0) return read == 0 ? null : throw new EndOfStreamException("Partial read from scrcpy video stream.");
                read += r;
            }
            return buf;
        }

        private byte[] ReadSome(int max)
        {
            var buf = new byte[max];
            int r = _stream.Read(buf, 0, max);
            if (r <= 0) return null;
            if (r == max) return buf;
            var trimmed = new byte[r];
            Buffer.BlockCopy(buf, 0, trimmed, 0, r);
            return trimmed;
        }
    }
}
