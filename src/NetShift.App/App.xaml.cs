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
        var updateFailure = NetShiftUpdateService.GetPendingUpdateFailure(e.Args);
        var window = new MainWindow();
        window.ContentRendered += (_, _) =>
        {
            NetShiftUpdateService.ConfirmUpdateStartup(updateBackupPath);
            if (updateFailure is not null)
                ThemedDialog.Show(window, $"Не удалось обновить NetShift: {updateFailure}", "Обновление NetShift",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        };
        MainWindow = window;
        window.Show();
    }
}
