using System.Windows;
using Slate.Notch;

namespace Slate;

public partial class App : Application
{
    private NotchWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _window = new NotchWindow();
        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _window?.Shutdown();
        base.OnExit(e);
    }
}
