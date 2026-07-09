using System;
using System.IO;

namespace AdbMcp.Logging
{
    /// <summary>
    /// Diagnostic logger. Everything goes to stderr because stdout is reserved
    /// exclusively for the newline-delimited JSON-RPC stream the MCP client reads.
    /// Writing a stray byte to stdout corrupts the protocol, so nothing in this
    /// project may ever call Console.Write* for anything but framed messages.
    /// </summary>
    public static class Log
    {
        public enum Level { Debug = 0, Info = 1, Warn = 2, Error = 3 }

        private static readonly object Gate = new object();
        public static Level Minimum = Level.Info;
        public static TextWriter Sink = Console.Error;

        public static void Debug(string message) => Write(Level.Debug, message);
        public static void Info(string message) => Write(Level.Info, message);
        public static void Warn(string message) => Write(Level.Warn, message);
        public static void Error(string message) => Write(Level.Error, message);

        public static void Error(string message, Exception ex)
            => Write(Level.Error, message + " :: " + ex.GetType().Name + ": " + ex.Message);

        private static void Write(Level level, string message)
        {
            if (level < Minimum) return;
            lock (Gate)
            {
                try
                {
                    Sink.WriteLine("[adb-mcp][" + level.ToString().ToLowerInvariant() + "] " + message);
                    Sink.Flush();
                }
                catch
                {
                    // Never let logging throw into the request loop.
                }
            }
        }
    }
}
