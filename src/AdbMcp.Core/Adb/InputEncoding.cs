using System.Text;

namespace AdbMcp.Adb
{
    /// <summary>
    /// Escapes text for <c>adb shell input text</c>. The device-side input command
    /// treats spaces and a set of shell metacharacters specially; spaces must be sent
    /// as %s and metacharacters backslash-escaped, or the typed string is corrupted.
    /// </summary>
    public static class InputEncoding
    {
        public static string EscapeForInputText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case ' ': sb.Append("%s"); break;
                    case '%': sb.Append("\\%"); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\'': sb.Append("\\'"); break;
                    case '(': sb.Append("\\("); break;
                    case ')': sb.Append("\\)"); break;
                    case '<': sb.Append("\\<"); break;
                    case '>': sb.Append("\\>"); break;
                    case '|': sb.Append("\\|"); break;
                    case ';': sb.Append("\\;"); break;
                    case '&': sb.Append("\\&"); break;
                    case '*': sb.Append("\\*"); break;
                    case '~': sb.Append("\\~"); break;
                    case '`': sb.Append("\\`"); break;
                    case '$': sb.Append("\\$"); break;
                    case '#': sb.Append("\\#"); break;
                    case '!': sb.Append("\\!"); break;
                    case '?': sb.Append("\\?"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
