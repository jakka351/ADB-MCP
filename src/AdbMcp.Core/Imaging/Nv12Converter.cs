using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AdbMcp.Imaging
{
    /// <summary>
    /// Converts an NV12 plane (the output format of the Media Foundation H.264 decoder)
    /// to a 24bpp RGB bitmap using the BT.601 full-range-corrected coefficients. Pure and
    /// unit-testable so the color math is validated independently of the COM decoder.
    ///
    /// NV12 layout: a Y plane (height rows of 'stride' bytes), then an interleaved
    /// UV plane (height/2 rows of 'stride' bytes, U then V per 2x2 luma block).
    /// </summary>
    public static class Nv12Converter
    {
        public static Bitmap ToBitmap(byte[] data, int width, int height, int stride)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (width <= 0 || height <= 0) throw new ArgumentException("Invalid frame dimensions.");
            if (stride < width) stride = width;

            int uvPlaneOffset = stride * height;
            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, width, height);
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int destStride = bd.Stride;
                byte[] row = new byte[destStride];
                for (int y = 0; y < height; y++)
                {
                    int yRow = y * stride;
                    int uvRow = uvPlaneOffset + (y / 2) * stride;
                    int di = 0;
                    for (int x = 0; x < width; x++)
                    {
                        int Y = data[yRow + x] - 16;
                        int uvIndex = uvRow + (x & ~1); // aligned to the 2-pixel UV pair
                        int U = data[uvIndex] - 128;
                        int V = data[uvIndex + 1] - 128;

                        int c298 = 298 * Y;
                        int r = (c298 + 409 * V + 128) >> 8;
                        int g = (c298 - 100 * U - 208 * V + 128) >> 8;
                        int b = (c298 + 516 * U + 128) >> 8;

                        // 24bppRgb in GDI+ is stored BGR.
                        row[di++] = Clamp(b);
                        row[di++] = Clamp(g);
                        row[di++] = Clamp(r);
                    }
                    Marshal.Copy(row, 0, bd.Scan0 + y * destStride, destStride);
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return bmp;
        }

        private static byte Clamp(int v) => v < 0 ? (byte)0 : (v > 255 ? (byte)255 : (byte)v);
    }
}
