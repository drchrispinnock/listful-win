using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Listful.App;

/// <summary>
/// Windows-only entry point, in place of the generated one: Velopack's install, update and
/// uninstall hooks must run (and may exit) before anything else, as in signet-windows.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Velopack.VelopackApp.Build().Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}
