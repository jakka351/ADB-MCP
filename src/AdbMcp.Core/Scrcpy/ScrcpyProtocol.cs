using System;
using System.Text;

namespace AdbMcp.Scrcpy
{
    /// <summary>
    /// Wire-format encoders for the scrcpy control channel and parsers for the video
    /// stream header. Layouts follow the scrcpy 2.x server protocol (network/big-endian
    /// byte order, matching the server's Java ByteBuffer). This is the "piggyback input
    /// on the same connection instead of shelling adb input" latency win — every message
    /// here is a few dozen bytes pushed down a held-open socket.
    ///
    /// NOTE: the scrcpy protocol is version-specific. These encoders target the 2.x
    /// server; the running scrcpy-server.jar must match (see ScrcpyOptions.ServerVersion).
    /// </summary>
    public static class ScrcpyProtocol
    {
        // Control message types (scrcpy 2.x).
        public const byte TYPE_INJECT_KEYCODE = 0;
        public const byte TYPE_INJECT_TEXT = 1;
        public const byte TYPE_INJECT_TOUCH_EVENT = 2;
        public const byte TYPE_INJECT_SCROLL_EVENT = 3;
        public const byte TYPE_BACK_OR_SCREEN_ON = 4;
        public const byte TYPE_EXPAND_NOTIFICATION_PANEL = 5;
        public const byte TYPE_EXPAND_SETTINGS_PANEL = 6;
        public const byte TYPE_COLLAPSE_PANELS = 7;
        public const byte TYPE_GET_CLIPBOARD = 8;
        public const byte TYPE_SET_CLIPBOARD = 9;
        public const byte TYPE_SET_SCREEN_POWER_MODE = 10;
        public const byte TYPE_ROTATE_DEVICE = 11;

        // Android MotionEvent actions.
        public const int ACTION_DOWN = 0;
        public const int ACTION_UP = 1;
        public const int ACTION_MOVE = 2;

        // Android KeyEvent actions.
        public const int KEY_ACTION_DOWN = 0;
        public const int KEY_ACTION_UP = 1;

        // Primary button (finger/left mouse) as an AMOTION_EVENT button bit.
        public const int BUTTON_PRIMARY = 1;

        // A synthetic pointer id for injected touches.
        public const long POINTER_ID_FINGER = -2L; // scrcpy uses -1 for mouse, -2 for a virtual finger

        public const ushort PRESSURE_MAX = 0xFFFF; // 1.0 in 16-bit fixed point

        /// <summary>Encode an inject-keycode control message (14 bytes).</summary>
        public static byte[] InjectKeycode(int action, int keycode, int repeat, int metaState)
        {
            var b = new BeBuffer(14);
            b.U8(TYPE_INJECT_KEYCODE);
            b.U8((byte)action);
            b.I32(keycode);
            b.I32(repeat);
            b.I32(metaState);
            return b.ToArray();
        }

        /// <summary>Encode an inject-text control message (5 + UTF-8 length).</summary>
        public static byte[] InjectText(string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text ?? "");
            var b = new BeBuffer(1 + 4 + utf8.Length);
            b.U8(TYPE_INJECT_TEXT);
            b.I32(utf8.Length);
            b.Bytes(utf8);
            return b.ToArray();
        }

        /// <summary>
        /// Encode an inject-touch control message (32 bytes). Coordinates are absolute
        /// device pixels; the server maps them against the reported screen size.
        /// </summary>
        public static byte[] InjectTouch(int action, long pointerId, int x, int y,
                                         int screenWidth, int screenHeight,
                                         ushort pressure, int actionButton, int buttons)
        {
            var b = new BeBuffer(32);
            b.U8(TYPE_INJECT_TOUCH_EVENT);
            b.U8((byte)action);
            b.I64(pointerId);
            WritePosition(b, x, y, screenWidth, screenHeight);
            b.U16(pressure);
            b.I32(actionButton);
            b.I32(buttons);
            return b.ToArray();
        }

        /// <summary>Encode an inject-scroll control message (21 bytes).</summary>
        public static byte[] InjectScroll(int x, int y, int screenWidth, int screenHeight,
                                          short hScroll, short vScroll, int buttons)
        {
            var b = new BeBuffer(21);
            b.U8(TYPE_INJECT_SCROLL_EVENT);
            WritePosition(b, x, y, screenWidth, screenHeight);
            b.I16(hScroll);
            b.I16(vScroll);
            b.I32(buttons);
            return b.ToArray();
        }

        /// <summary>Encode a back-or-screen-on control message (2 bytes).</summary>
        public static byte[] BackOrScreenOn(int action)
        {
            var b = new BeBuffer(2);
            b.U8(TYPE_BACK_OR_SCREEN_ON);
            b.U8((byte)action);
            return b.ToArray();
        }

        // position = x:int32, y:int32, width:uint16, height:uint16 (12 bytes)
        private static void WritePosition(BeBuffer b, int x, int y, int width, int height)
        {
            b.I32(x);
            b.I32(y);
            b.U16((ushort)width);
            b.U16((ushort)height);
        }

        /// <summary>Convert a normalized 0..1 pressure to scrcpy's 16-bit fixed point.</summary>
        public static ushort ToFixedPointPressure(double value)
        {
            if (value <= 0) return 0;
            long i = (long)(value * 0x10000);
            if (i >= 0xFFFF) return 0xFFFF;
            return (ushort)i;
        }
    }

    /// <summary>Minimal big-endian buffer writer for the scrcpy wire format.</summary>
    public sealed class BeBuffer
    {
        private readonly byte[] _buf;
        private int _pos;

        public BeBuffer(int capacity) { _buf = new byte[capacity]; }

        public void U8(byte v) => _buf[_pos++] = v;

        public void U16(ushort v)
        {
            _buf[_pos++] = (byte)(v >> 8);
            _buf[_pos++] = (byte)v;
        }

        public void I16(short v) => U16((ushort)v);

        public void I32(int v)
        {
            _buf[_pos++] = (byte)(v >> 24);
            _buf[_pos++] = (byte)(v >> 16);
            _buf[_pos++] = (byte)(v >> 8);
            _buf[_pos++] = (byte)v;
        }

        public void I64(long v)
        {
            _buf[_pos++] = (byte)(v >> 56);
            _buf[_pos++] = (byte)(v >> 48);
            _buf[_pos++] = (byte)(v >> 40);
            _buf[_pos++] = (byte)(v >> 32);
            _buf[_pos++] = (byte)(v >> 24);
            _buf[_pos++] = (byte)(v >> 16);
            _buf[_pos++] = (byte)(v >> 8);
            _buf[_pos++] = (byte)v;
        }

        public void Bytes(byte[] b)
        {
            Buffer.BlockCopy(b, 0, _buf, _pos, b.Length);
            _pos += b.Length;
        }

        public byte[] ToArray()
        {
            if (_pos == _buf.Length) return _buf;
            var trimmed = new byte[_pos];
            Buffer.BlockCopy(_buf, 0, trimmed, 0, _pos);
            return trimmed;
        }
    }

    /// <summary>Big-endian read helpers for parsing the video stream.</summary>
    public static class BeReader
    {
        public static long ReadI64(byte[] b, int off)
            => ((long)b[off] << 56) | ((long)b[off + 1] << 48) | ((long)b[off + 2] << 40) | ((long)b[off + 3] << 32)
             | ((long)b[off + 4] << 24) | ((long)b[off + 5] << 16) | ((long)b[off + 6] << 8) | b[off + 7];

        public static uint ReadU32(byte[] b, int off)
            => ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];
    }
}
