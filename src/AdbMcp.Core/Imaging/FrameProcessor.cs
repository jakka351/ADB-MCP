using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace AdbMcp.Imaging
{
    /// <summary>Result of encoding a captured frame for delivery to the model.</summary>
    public sealed class EncodedFrame
    {
        public byte[] Data { get; set; }
        public string MimeType { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }

        public string Base64 => Convert.ToBase64String(Data);
    }

    /// <summary>
    /// Turns raw screencap PNG bytes into a token-economical frame: optionally
    /// downscaled to a max edge and re-encoded as JPEG. This is the "sample only
    /// when needed, keep it cheap" half of the hybrid perception strategy.
    /// </summary>
    public static class FrameProcessor
    {
        public static EncodedFrame Encode(byte[] pngBytes, string format, int maxDimension, int jpegQuality)
        {
            if (pngBytes == null || pngBytes.Length == 0)
                throw new ArgumentException("No image bytes to encode.");

            using (var inMs = new MemoryStream(pngBytes))
            using (var src = new Bitmap(inMs))
            {
                return Encode(src, format, maxDimension, jpegQuality);
            }
        }

        /// <summary>Downscale + re-encode an in-memory bitmap (e.g. a decoded scrcpy frame).</summary>
        public static EncodedFrame Encode(Bitmap src, string format, int maxDimension, int jpegQuality)
        {
            if (src == null) throw new ArgumentException("No bitmap to encode.");
            {
                int sw = src.Width, sh = src.Height;
                int longest = Math.Max(sw, sh);

                double scale = 1.0;
                if (maxDimension > 0 && longest > maxDimension)
                    scale = (double)maxDimension / longest;

                Bitmap target = src;
                bool disposeTarget = false;
                if (scale < 1.0)
                {
                    int nw = Math.Max(1, (int)Math.Round(sw * scale));
                    int nh = Math.Max(1, (int)Math.Round(sh * scale));
                    var resized = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.DrawImage(src, 0, 0, nw, nh);
                    }
                    target = resized;
                    disposeTarget = true;
                }

                try
                {
                    using (var outMs = new MemoryStream())
                    {
                        string mime;
                        if (string.Equals(format, "png", StringComparison.OrdinalIgnoreCase))
                        {
                            target.Save(outMs, ImageFormat.Png);
                            mime = "image/png";
                        }
                        else
                        {
                            var enc = GetEncoder(ImageFormat.Jpeg);
                            using (var ps = new EncoderParameters(1))
                            {
                                ps.Param[0] = new EncoderParameter(Encoder.Quality, (long)Clamp(jpegQuality, 1, 100));
                                target.Save(outMs, enc, ps);
                            }
                            mime = "image/jpeg";
                        }

                        return new EncodedFrame
                        {
                            Data = outMs.ToArray(),
                            MimeType = mime,
                            Width = target.Width,
                            Height = target.Height,
                            SourceWidth = sw,
                            SourceHeight = sh,
                        };
                    }
                }
                finally
                {
                    if (disposeTarget) target.Dispose();
                }
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
                if (codec.FormatID == format.Guid) return codec;
            throw new InvalidOperationException("No image encoder available for " + format);
        }
    }
}
