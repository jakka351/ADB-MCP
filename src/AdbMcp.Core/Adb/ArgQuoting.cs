using System.Collections.Generic;
using System.Text;

namespace AdbMcp.Adb
{
    /// <summary>
    /// Windows command-line argument quoting. .NET Framework 4.8 has no
    /// ProcessStartInfo.ArgumentList, so we build the Arguments string ourselves
    /// following the CommandLineToArgvW rules the CRT uses to split argv.
    /// </summary>
    public static class ArgQuoting
    {
        public static string Join(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var a in args)
            {
                if (!first) sb.Append(' ');
                first = false;
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        public static string Quote(string arg)
        {
            if (arg == null) arg = string.Empty;

            // No quoting needed when the token has no whitespace or quote characters.
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; ; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { i++; backslashes++; }

                if (i == arg.Length)
                {
                    // Escape all trailing backslashes so they don't escape the closing quote.
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (arg[i] == '"')
                {
                    // Escape the backslashes plus the quote itself.
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
