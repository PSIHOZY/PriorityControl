using System;
using System.Linq;
using System.Threading;
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

            bool createdNew;
            using (var mutex = new Mutex(true, @"Global\PriorityControl.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(startedFromStartup, args));
            }
        }
    }
}
