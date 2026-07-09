using AdbMcp.Logging;

namespace AdbMcp.Adb
{
    /// <summary>Captures the live view hierarchy — the primary, token-cheap perception source.</summary>
    public static class Perception
    {
        private const string RemotePath = "/sdcard/_adbmcp_ui.xml";

        public static UiHierarchy DumpHierarchy(AdbClient adb)
        {
            // uiautomator writes the dump to a file; we then read it back. Using an explicit
            // path avoids ambiguity across OEM defaults.
            var dump = adb.Shell("uiautomator dump " + RemotePath);
            string xml;
            try
            {
                xml = adb.ShellText("cat " + RemotePath);
            }
            catch (AdbException)
            {
                // Some builds ignore the path argument and write to the default location.
                xml = adb.ShellText("cat /sdcard/window_dump.xml");
            }
            finally
            {
                try { adb.Shell("rm -f " + RemotePath); } catch { }
            }

            if (string.IsNullOrWhiteSpace(xml))
            {
                var hint = dump.StdOutText;
                throw new AdbException(
                    "uiautomator dump returned no XML. " +
                    (string.IsNullOrEmpty(hint) ? "" : "adb said: " + hint + ". ") +
                    "A secure/DRM window may block dumping; try get_screenshot instead.");
            }

            Log.Debug("Parsed UI hierarchy (" + xml.Length + " chars).");
            return UiHierarchy.Parse(xml);
        }
    }
}
