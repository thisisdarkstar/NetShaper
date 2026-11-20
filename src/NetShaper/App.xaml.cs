using System.Windows;
using NetShaper.Core;

namespace NetShaper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Auto-heal any stale proxy settings from a previous abnormal termination
        WinInetProxyManager.SanitizeProxyOnStartup();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Guarantee system proxy is restored on exit
        WinInetProxyManager.RestoreDirectConnection();
        base.OnExit(e);
    }
}
