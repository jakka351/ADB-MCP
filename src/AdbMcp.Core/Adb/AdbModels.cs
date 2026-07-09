using System;
using System.Text;

namespace AdbMcp.Adb
{
    /// <summary>Raised when an adb invocation fails, times out, or the binary is missing.</summary>
    public sealed class AdbException : Exception
    {
        public AdbException(string message) : base(message) { }
        public AdbException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Captured result of a single adb process invocation.</summary>
    public sealed class ProcessResult
    {
        public int ExitCode { get; }
        public byte[] StdOut { get; }
        public string StdErr { get; }

        public ProcessResult(int exitCode, byte[] stdOut, string stdErr)
        {
            ExitCode = exitCode;
            StdOut = stdOut ?? Array.Empty<byte>();
            StdErr = stdErr ?? string.Empty;
        }

        public bool Ok => ExitCode == 0;

        /// <summary>stdout decoded as UTF-8 text, trailing newlines trimmed.</summary>
        public string StdOutText => Encoding.UTF8.GetString(StdOut).TrimEnd('\r', '\n');

        public string DescribeFailure(string what)
        {
            var sb = new StringBuilder();
            sb.Append(what).Append(" failed (exit ").Append(ExitCode).Append(')');
            var err = StdErr?.Trim();
            if (!string.IsNullOrEmpty(err)) sb.Append(": ").Append(err);
            return sb.ToString();
        }
    }

    /// <summary>A device row from <c>adb devices -l</c>.</summary>
    public sealed class DeviceInfo
    {
        public string Serial { get; set; }
        public string State { get; set; }      // device | offline | unauthorized ...
        public string Model { get; set; }
        public string Product { get; set; }
        public string TransportId { get; set; }

        public bool IsUsable => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Serial).Append("  [").Append(State).Append(']');
            if (!string.IsNullOrEmpty(Model)) sb.Append("  model=").Append(Model);
            if (!string.IsNullOrEmpty(TransportId)) sb.Append("  transport=").Append(TransportId);
            return sb.ToString();
        }
    }
}
