using System;
using System.Drawing;
using AdbMcp.Imaging;
using AdbMcp.Logging;

namespace AdbMcp.Scrcpy
{
    /// <summary>
    /// Bridges the scrcpy video socket to the model's frame supply: consumes demuxed
    /// packets, feeds config (SPS/PPS) and frames to the decoder, and keeps the newest
    /// decoded bitmap available for on-demand sampling. If the decoder is unavailable,
    /// HasFrame stays false and the caller falls back to screencap.
    /// </summary>
    public sealed class ScrcpyFrameSource : IVideoPacketSink, IDisposable
    {
        private readonly IVideoDecoder _decoder;
        private readonly object _gate = new object();
        private Bitmap _latest;

        public DateTime LastFrameUtc { get; private set; }
        public long FramesReceived { get; private set; }
        public long FramesDecoded { get; private set; }
        public bool DecoderAvailable => _decoder != null && _decoder.IsAvailable;

        public ScrcpyFrameSource(IVideoDecoder decoder)
        {
            _decoder = decoder ?? new NullVideoDecoder();
        }

        public bool HasFrame
        {
            get { lock (_gate) return _latest != null; }
        }

        public void OnCodecMeta(VideoCodecMeta meta)
        {
            if (meta == null) return;
            Log.Info("scrcpy video: " + meta.CodecId + " " + meta.Width + "x" + meta.Height);
            try { _decoder.Initialize(meta.Width, meta.Height); }
            catch (Exception ex) { Log.Warn("Decoder initialise failed: " + ex.Message); }
        }

        public void OnPacket(VideoPacket packet)
        {
            if (packet?.Data == null) return;
            FramesReceived++;

            if (packet.IsConfig)
            {
                _decoder.SubmitConfig(packet.Data);
                return;
            }

            if (!_decoder.IsAvailable) return;

            var bmp = _decoder.DecodeFrame(packet.Data);
            if (bmp == null) return;

            lock (_gate)
            {
                _latest?.Dispose();
                _latest = bmp;
                LastFrameUtc = DateTime.UtcNow;
            }
            FramesDecoded++;
        }

        public void OnClosed()
        {
            Log.Debug("scrcpy frame source closed (" + FramesDecoded + "/" + FramesReceived + " frames decoded).");
        }

        /// <summary>Return a private copy of the newest decoded frame, or null if none yet.</summary>
        public Bitmap CaptureLatest()
        {
            lock (_gate)
            {
                return _latest == null ? null : new Bitmap(_latest);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _latest?.Dispose();
                _latest = null;
            }
            try { _decoder.Dispose(); } catch { }
        }
    }
}
