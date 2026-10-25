using System.Windows;
using System.Windows.Threading;

namespace PartitionDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        Theme.Start(this);
        base.OnStartup(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "PartitionDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
