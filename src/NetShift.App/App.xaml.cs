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

        if (NetShiftBootstrapper.TryStartInstalledCopy(e.Args))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var updateBackupPath = NetShiftUpdateService.GetPendingUpdateBackup(e.Args, Environment.ProcessPath);
        var window = new MainWindow();
        window.ContentRendered += (_, _) => NetShiftUpdateService.ConfirmUpdateStartup(updateBackupPath);
        MainWindow = window;
        window.Show();
    }
}
