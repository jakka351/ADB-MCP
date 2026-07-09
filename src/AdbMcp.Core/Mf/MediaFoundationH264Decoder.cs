using System;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using AdbMcp.Imaging;
using AdbMcp.Logging;

namespace AdbMcp.Mf
{
    /// <summary>
    /// H.264 -&gt; RGB decoder backed by the in-box Media Foundation decoder MFT
    /// (CLSID_CMSH264DecoderMFT). Output is negotiated to NV12 and converted with the
    /// unit-tested <see cref="Nv12Converter"/>.
    ///
    /// This is the only dependency-free way to decode H.264 on Windows, but the COM
    /// interop cannot be exercised without a live stream, so it is treated as
    /// device-verification-pending: every entry point is wrapped (including corrupted-
    /// state exceptions) so any failure sets IsAvailable=false and the frame pipeline
    /// falls back to screencap — the decoder can never take down the server.
    /// </summary>
    public sealed class MediaFoundationH264Decoder : IVideoDecoder
    {
        private const int OUT_PRODUCED = 0;
        private const int OUT_NEED_MORE = 1;
        private const int OUT_STREAM_CHANGE = 2;

        private IMFTransform _mft;
        private bool _mfStarted;
        private int _width, _height, _stride;

        public bool IsAvailable { get; private set; }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public bool Initialize(int width, int height)
        {
            try
            {
                Check(Mf.MFStartup(Mf.MF_VERSION, Mf.MFSTARTUP_LITE));
                _mfStarted = true;

                var type = Type.GetTypeFromCLSID(Mf.CLSID_H264Decoder);
                _mft = (IMFTransform)Activator.CreateInstance(type);

                _width = width > 0 ? width : 1080;
                _height = height > 0 ? height : 1920;
                _stride = _width;

                ConfigureInputType();
                NegotiateOutput();

                Check(_mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero));
                Check(_mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero));

                IsAvailable = true;
                Log.Info("Media Foundation H.264 decoder initialised (" + _width + "x" + _height + ", stride " + _stride + ").");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Media Foundation H.264 decoder unavailable; frames will fall back to screencap: " + ex.Message);
                IsAvailable = false;
                return false;
            }
        }

        private void ConfigureInputType()
        {
            Check(Mf.MFCreateMediaType(out IMFMediaType inType));
            Guid major = Mf.MFMediaType_Video, h264 = Mf.MFVideoFormat_H264;
            Guid kMajor = Mf.MF_MT_MAJOR_TYPE, kSub = Mf.MF_MT_SUBTYPE, kSize = Mf.MF_MT_FRAME_SIZE, kInter = Mf.MF_MT_INTERLACE_MODE;

            Check(inType.SetGUID(ref kMajor, ref major));
            Check(inType.SetGUID(ref kSub, ref h264));
            Check(inType.SetUINT64(ref kSize, Mf.PackSize(_width, _height)));
            inType.SetUINT32(ref kInter, Mf.MFVideoInterlace_Progressive);
            Check(_mft.SetInputType(0, inType, 0));
        }

        private void NegotiateOutput()
        {
            for (uint i = 0; ; i++)
            {
                int hr = _mft.GetOutputAvailableType(0, i, out IMFMediaType t);
                if (hr != Mf.S_OK || t == null) break;

                Guid kSub = Mf.MF_MT_SUBTYPE;
                if (t.GetGUID(ref kSub, out Guid sub) == Mf.S_OK && sub == Mf.MFVideoFormat_NV12)
                {
                    Guid kSize = Mf.MF_MT_FRAME_SIZE;
                    t.SetUINT64(ref kSize, Mf.PackSize(_width, _height));
                    Check(_mft.SetOutputType(0, t, 0));
                    ReadOutputDimensions();
                    return;
                }
            }
            throw new NotSupportedException("The H.264 decoder MFT did not offer NV12 output.");
        }

        private void ReadOutputDimensions()
        {
            if (_mft.GetOutputCurrentType(0, out IMFMediaType cur) != Mf.S_OK || cur == null) return;

            Guid kSize = Mf.MF_MT_FRAME_SIZE;
            if (cur.GetUINT64(ref kSize, out ulong packed) == Mf.S_OK)
            {
                _width = (int)(packed >> 32);
                _height = (int)(packed & 0xFFFFFFFF);
            }
            Guid kStride = Mf.MF_MT_DEFAULT_STRIDE;
            if (cur.GetUINT32(ref kStride, out uint stride) == Mf.S_OK && stride > 0)
                _stride = (int)stride;
            if (_stride < _width) _stride = _width;
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public void SubmitConfig(byte[] annexB)
        {
            if (!IsAvailable || annexB == null || annexB.Length == 0) return;
            try { FeedInput(annexB); }
            catch (Exception ex) { Log.Warn("H.264 config submit failed: " + ex.Message); IsAvailable = false; }
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public Bitmap DecodeFrame(byte[] annexB)
        {
            if (!IsAvailable || annexB == null || annexB.Length == 0) return null;
            try
            {
                FeedInput(annexB);
                Bitmap last = null;
                for (int guard = 0; guard < 16; guard++)
                {
                    int outcome = TryProcessOutput(out Bitmap b);
                    if (outcome == OUT_NEED_MORE) break;
                    if (outcome == OUT_STREAM_CHANGE) continue;
                    if (b != null) { last?.Dispose(); last = b; }
                }
                return last;
            }
            catch (Exception ex)
            {
                Log.Warn("H.264 decode failed; disabling decoder: " + ex.Message);
                IsAvailable = false;
                return null;
            }
        }

        private void FeedInput(byte[] data)
        {
            Check(Mf.MFCreateSample(out IMFSample sample));
            Check(Mf.MFCreateMemoryBuffer((uint)data.Length, out IMFMediaBuffer buf));
            Check(buf.Lock(out IntPtr ptr, out _, out _));
            Marshal.Copy(data, 0, ptr, data.Length);
            buf.Unlock();
            Check(buf.SetCurrentLength((uint)data.Length));
            Check(sample.AddBuffer(buf));

            int hr = _mft.ProcessInput(0, sample, 0);
            if (hr == Mf.MF_E_NOTACCEPTING)
            {
                // Decoder is holding output; drain then retry once.
                while (TryProcessOutput(out Bitmap _b) == OUT_PRODUCED) { _b?.Dispose(); }
                hr = _mft.ProcessInput(0, sample, 0);
            }
            Check(hr);
        }

        private int TryProcessOutput(out Bitmap bitmap)
        {
            bitmap = null;
            Check(_mft.GetOutputStreamInfo(0, out MFT_OUTPUT_STREAM_INFO info));
            bool mftAllocates = (info.dwFlags & Mf.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

            IMFSample outSample = null;
            if (!mftAllocates)
            {
                Check(Mf.MFCreateSample(out outSample));
                uint cb = Math.Max(info.cbSize, (uint)(_stride * _height * 3 / 2));
                Check(Mf.MFCreateMemoryBuffer(cb, out IMFMediaBuffer outBuf));
                Check(outSample.AddBuffer(outBuf));
            }

            var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
            buffers[0].dwStreamID = 0;
            buffers[0].pSample = outSample;

            int hr = _mft.ProcessOutput(0, 1, buffers, out _);
            if (hr == Mf.MF_E_TRANSFORM_NEED_MORE_INPUT) return OUT_NEED_MORE;
            if (hr == Mf.MF_E_TRANSFORM_STREAM_CHANGE) { NegotiateOutput(); return OUT_STREAM_CHANGE; }
            Check(hr);

            var produced = buffers[0].pSample as IMFSample;
            if (produced == null) return OUT_NEED_MORE;
            bitmap = ReadSampleToBitmap(produced);
            return OUT_PRODUCED;
        }

        private Bitmap ReadSampleToBitmap(IMFSample sample)
        {
            Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buf));
            Check(buf.Lock(out IntPtr ptr, out _, out uint curLen));
            try
            {
                var nv12 = new byte[curLen];
                Marshal.Copy(ptr, nv12, 0, (int)curLen);
                return Nv12Converter.ToBitmap(nv12, _width, _height, _stride);
            }
            finally
            {
                buf.Unlock();
            }
        }

        private static void Check(int hr)
        {
            if (hr < 0) throw new COMException("Media Foundation call failed (0x" + hr.ToString("X8") + ").", hr);
        }

        public void Dispose()
        {
            try { if (_mft != null) Marshal.ReleaseComObject(_mft); } catch { }
            _mft = null;
            try { if (_mfStarted) Mf.MFShutdown(); } catch { }
            _mfStarted = false;
            IsAvailable = false;
        }
    }
}
