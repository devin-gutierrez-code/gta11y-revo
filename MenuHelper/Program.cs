using System;
using System.Windows.Forms;

namespace GTA11Y.MenuHelper
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MenuHelperForm());
        }
    }
}
