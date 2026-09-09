using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Crosio.Windows.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var singleInstance = SingleInstanceCoordinator.RegisterOrRedirectAsync()
            .GetAwaiter()
            .GetResult();

        if (singleInstance is null)
        {
            return;
        }

        Application.Start(_initialization =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(dispatcherQueue));
            _ = new App(singleInstance);
        });
    }
}
