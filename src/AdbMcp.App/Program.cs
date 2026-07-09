using System;
using System.Windows.Forms;

namespace AdbMcp.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Optional: pass a device serial/address as the first arg.
            if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
                Adb.Serial = args[0];

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
