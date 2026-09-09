using Microsoft.Windows.AppLifecycle;

namespace Crosio.Windows.App;

internal sealed class SingleInstanceCoordinator
{
    private const string MainInstanceKey = "Crosio.Main";
    private readonly AppInstance _instance;

    private SingleInstanceCoordinator(AppInstance instance, AppActivationArguments initialActivation)
    {
        _instance = instance;
        InitialActivation = initialActivation;
        _instance.Activated += OnActivated;
    }

    public event EventHandler<AppActivationArguments>? Activated;

    public AppActivationArguments InitialActivation { get; }

    public static async Task<SingleInstanceCoordinator?> RegisterOrRedirectAsync()
    {
        var current = AppInstance.GetCurrent();
        var activation = current.GetActivatedEventArgs();
        var mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);

        if (!mainInstance.IsCurrent)
        {
            await mainInstance.RedirectActivationToAsync(activation);
            return null;
        }

        return new SingleInstanceCoordinator(mainInstance, activation);
    }

    private void OnActivated(object? sender, AppActivationArguments arguments) =>
        Activated?.Invoke(this, arguments);
}
