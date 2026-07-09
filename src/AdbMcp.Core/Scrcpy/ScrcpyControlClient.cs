using System;
using System.IO;
using System.Threading;
using AdbMcp.Logging;

namespace AdbMcp.Scrcpy
{
    /// <summary>
    /// Sends input over the held-open scrcpy control socket — the latency win from the
    /// feasibility brief: tap/swipe/type/keys become a few dozen bytes down an existing
    /// connection instead of spawning an <c>adb shell input</c> per action.
    ///
    /// Touch coordinates are device pixels; ScreenWidth/Height must be the device
    /// resolution so the server's position mapping is identity.
    /// </summary>
    public sealed class ScrcpyControlClient
    {
        private readonly Stream _stream;
        private readonly object _writeGate = new object();

        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }

        public ScrcpyControlClient(Stream controlStream, int screenWidth, int screenHeight)
        {
            _stream = controlStream ?? throw new ArgumentNullException(nameof(controlStream));
            ScreenWidth = screenWidth;
            ScreenHeight = screenHeight;
        }

        private void Send(byte[] message)
        {
            lock (_writeGate)
            {
                _stream.Write(message, 0, message.Length);
                _stream.Flush();
            }
        }

        public void Tap(int x, int y)
        {
            Send(ScrcpyProtocol.InjectTouch(ScrcpyProtocol.ACTION_DOWN, ScrcpyProtocol.POINTER_ID_FINGER,
                x, y, ScreenWidth, ScreenHeight, ScrcpyProtocol.PRESSURE_MAX,
                ScrcpyProtocol.BUTTON_PRIMARY, ScrcpyProtocol.BUTTON_PRIMARY));
            Thread.Sleep(40);
            Send(ScrcpyProtocol.InjectTouch(ScrcpyProtocol.ACTION_UP, ScrcpyProtocol.POINTER_ID_FINGER,
                x, y, ScreenWidth, ScreenHeight, 0, ScrcpyProtocol.BUTTON_PRIMARY, 0));
        }

        public void LongPress(int x, int y, int holdMs = 700)
        {
            Send(ScrcpyProtocol.InjectTouch(ScrcpyProtocol.ACTION_DOWN, ScrcpyProtocol.POINTER_ID_FINGER,
                x, y, ScreenWidth, ScreenHeight, ScrcpyProtocol.PRESSURE_MAX,
                ScrcpyProtocol.BUTTON_PRIMARY, ScrcpyProtocol.BUTTON_PRIMARY));
            Thread.Sleep(holdMs);
            Send(ScrcpyProtocol.InjectTouch(ScrcpyProtocol.ACTION_UP, ScrcpyProtocol.POINTER_ID_FINGER,
                x, y, ScreenWidth, ScreenHeight, 0, ScrcpyProtocol.BUTTON_PRIMARY, 0));
        }

        public void Swipe(int x1, int y1, int x2, int y2, int durationMs = 300)
        {
            const int steps = 16;
            Send(ScrcpyProtocol.InjectTouch(ScrcpyProtocol.ACTION_DOWN, ScrcpyProtocol.POINTER_ID_FINGER,
                x1, y1, ScreenWidth, ScreenHeight, ScrcpyProtocol.PRESSURE_MAX,
                ScrcpyProtocol.BUTTON_PRIMARY, ScrcpyProtocol.BUTTON_PRIMARY));

            int stepDelay = Math.Max(1, durationMs / steps);
            for (int i = 1; i <= steps; i++)
            {
                int x = x1 + (x2 - x1) * i / steps;
                int y = y1 + (y2 - y1) * i / steps;
                Send(ScrcpyProtocol.InjectTouch(ScrcpyProtocol.ACTION_MOVE, ScrcpyProtocol.POINTER_ID_FINGER,
                    x, y, ScreenWidth, ScreenHeight, ScrcpyProtocol.PRESSURE_MAX,
                    0, ScrcpyProtocol.BUTTON_PRIMARY));
                Thread.Sleep(stepDelay);
            }
            Send(ScrcpyProtocol.InjectTouch(ScrcpyProtocol.ACTION_UP, ScrcpyProtocol.POINTER_ID_FINGER,
                x2, y2, ScreenWidth, ScreenHeight, 0, ScrcpyProtocol.BUTTON_PRIMARY, 0));
        }

        public void PressKey(int keycode)
        {
            Send(ScrcpyProtocol.InjectKeycode(ScrcpyProtocol.KEY_ACTION_DOWN, keycode, 0, 0));
            Send(ScrcpyProtocol.InjectKeycode(ScrcpyProtocol.KEY_ACTION_UP, keycode, 0, 0));
        }

        public void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // scrcpy inject-text caps at 300 UTF-8 bytes; chunk longer input.
            const int maxBytes = 280;
            int i = 0;
            while (i < text.Length)
            {
                int len = Math.Min(maxBytes, text.Length - i);
                Send(ScrcpyProtocol.InjectText(text.Substring(i, len)));
                i += len;
            }
        }

        public void BackOrScreenOn()
        {
            Send(ScrcpyProtocol.BackOrScreenOn(ScrcpyProtocol.ACTION_DOWN));
            Send(ScrcpyProtocol.BackOrScreenOn(ScrcpyProtocol.ACTION_UP));
        }

        public void Close()
        {
            try { _stream.Dispose(); } catch { }
            Log.Debug("scrcpy control channel closed.");
        }
    }
}
