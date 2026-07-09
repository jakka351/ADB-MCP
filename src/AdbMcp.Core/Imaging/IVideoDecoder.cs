using System;
using System.Drawing;

namespace AdbMcp.Imaging
{
    /// <summary>
    /// Turns Annex-B H.264 access units (from the scrcpy video socket) into RGB bitmaps.
    /// Implementations wrap a platform decoder; the seam lets the frame pipeline degrade
    /// to screencap when no decoder is available.
    /// </summary>
    public interface IVideoDecoder : IDisposable
    {
        /// <summary>True once the decoder is initialised and can produce frames.</summary>
        bool IsAvailable { get; }

        /// <summary>Initialise for a stream of the given dimensions. Returns false if unavailable.</summary>
        bool Initialize(int width, int height);

        /// <summary>Feed codec configuration (SPS/PPS) received as a config packet.</summary>
        void SubmitConfig(byte[] annexB);

        /// <summary>Decode a frame access unit. Returns the newest decoded bitmap, or null if none is ready yet.</summary>
        Bitmap DecodeFrame(byte[] annexB);
    }

    /// <summary>
    /// No-op decoder used when no platform decoder is wired. The scrcpy video socket is
    /// still fully consumed and demuxed, but frames for the model come from screencap.
    /// Swap in MediaFoundationH264Decoder (Windows) or an FFmpeg-backed decoder here.
    /// </summary>
    public sealed class NullVideoDecoder : IVideoDecoder
    {
        public bool IsAvailable => false;
        public bool Initialize(int width, int height) => false;
        public void SubmitConfig(byte[] annexB) { }
        public Bitmap DecodeFrame(byte[] annexB) => null;
        public void Dispose() { }
    }
}
