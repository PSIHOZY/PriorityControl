using System;
using System.Linq;
using System.Windows.Forms;
using PriorityControl.UI;

namespace PriorityControl
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool startedFromStartup = args.Any(arg =>
                string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(startedFromStartup, args));
        }
    }
}
