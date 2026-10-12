using System.Windows;
using NetShift.App.Services;

namespace NetShift.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (AdminCommandRouter.TryHandle(e.Args))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
