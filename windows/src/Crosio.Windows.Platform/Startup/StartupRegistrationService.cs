using global::Windows.ApplicationModel;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Crosio.Windows.Platform.Startup;

public enum StartupRegistrationStatus
{
    Disabled,
    Enabled,
    RequiresUserAction,
    DisabledByPolicy,
    RegistrationMismatch,
    Unavailable,
}

public sealed record StartupRegistrationResult(
    StartupRegistrationStatus Status,
    string Message)
{
    public bool IsRequested => Status is
        StartupRegistrationStatus.Enabled or
        StartupRegistrationStatus.RequiresUserAction;
}

public interface IStartupRegistrationService
{
    Task<StartupRegistrationResult> GetStatusAsync();

    Task<StartupRegistrationResult> SetEnabledAsync(bool enabled);

    void OpenSystemStartupSettings();
}

/// <summary>
/// Uses the package-declared StartupTask in MSIX builds and an HKCU Run value
/// for unpackaged developer builds. Neither path requires administrator rights.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class StartupRegistrationService : IStartupRegistrationService
{
    public const string DefaultTaskId = "CrosioStartupTask";
    public const string RegistryValueName = "Crosio";

    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _taskId;
    private readonly string _executablePath;
    private readonly Func<bool> _hasPackageIdentity;

    public StartupRegistrationService(
        string taskId = DefaultTaskId,
        string? executablePath = null,
        Func<bool>? hasPackageIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        _taskId = taskId;
        _executablePath = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Windows did not report the Crosio executable path.");
        _hasPackageIdentity = hasPackageIdentity ?? HasPackageIdentity;
    }

    public async Task<StartupRegistrationResult> GetStatusAsync()
    {
        if (_hasPackageIdentity())
        {
            try
            {
                var task = await StartupTask.GetAsync(_taskId);
                return MapPackagedState(task.State.ToString());
            }
            catch (Exception exception)
            {
                return new StartupRegistrationResult(
                    StartupRegistrationStatus.Unavailable,
                    $"Windows could not read the packaged startup task: {exception.Message}");
            }
        }

        return ReadRegistryStatus();
    }

    public async Task<StartupRegistrationResult> SetEnabledAsync(bool enabled)
    {
        if (_hasPackageIdentity())
        {
            return await SetPackagedEnabledAsync(enabled);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryRunKey, writable: true)
                ?? throw new InvalidOperationException("Windows did not open the current user's startup registry key.");
            if (enabled)
            {
                key.SetValue(RegistryValueName, BuildBackgroundCommand(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RegistryValueName, throwOnMissingValue: false);
            }

            return ReadRegistryStatus();
        }
        catch (Exception exception)
        {
            return new StartupRegistrationResult(
                StartupRegistrationStatus.Unavailable,
                $"Windows could not update login startup: {exception.Message}");
        }
    }

    public void OpenSystemStartupSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:startupapps")
        {
            UseShellExecute = true,
        });
    }

    private async Task<StartupRegistrationResult> SetPackagedEnabledAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(_taskId);
            var currentState = task.State.ToString();
            if (!enabled)
            {
                task.Disable();
                return MapPackagedState(task.State.ToString());
            }

            if (currentState == "DisabledByUser")
            {
                return new StartupRegistrationResult(
                    StartupRegistrationStatus.RequiresUserAction,
                    "Startup was disabled in Windows Settings or Task Manager. Re-enable Crosio there.");
            }

            var state = await task.RequestEnableAsync();
            return MapPackagedState(state.ToString());
        }
        catch (Exception exception)
        {
            return new StartupRegistrationResult(
                StartupRegistrationStatus.Unavailable,
                $"Windows could not update the packaged startup task: {exception.Message}");
        }
    }

    private StartupRegistrationResult ReadRegistryStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: false);
            var value = key?.GetValue(RegistryValueName) as string;
            if (value is null)
            {
                return new StartupRegistrationResult(
                    StartupRegistrationStatus.Disabled,
                    "Crosio will not start when you sign in to Windows.");
            }

            if (!string.Equals(value, BuildBackgroundCommand(), StringComparison.OrdinalIgnoreCase))
            {
                return new StartupRegistrationResult(
                    StartupRegistrationStatus.RegistrationMismatch,
                    "A different Crosio startup command is registered. Toggle the setting to repair it.");
            }

            return new StartupRegistrationResult(
                StartupRegistrationStatus.Enabled,
                "Crosio will start in the notification area when you sign in to Windows.");
        }
        catch (Exception exception)
        {
            return new StartupRegistrationResult(
                StartupRegistrationStatus.Unavailable,
                $"Windows could not read login startup: {exception.Message}");
        }
    }

    private string BuildBackgroundCommand()
    {
        if (_executablePath.IndexOf('"') >= 0 ||
            !_executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(_executablePath))
        {
            throw new InvalidOperationException("The Crosio executable path is not safe to register.");
        }

        return $"\"{_executablePath}\" --background";
    }

    private static StartupRegistrationResult MapPackagedState(string state) => state switch
    {
        "Enabled" or "EnabledByPolicy" => new StartupRegistrationResult(
            StartupRegistrationStatus.Enabled,
            "Crosio will start in the notification area when you sign in to Windows."),
        "Disabled" => new StartupRegistrationResult(
            StartupRegistrationStatus.Disabled,
            "Crosio will not start when you sign in to Windows."),
        "DisabledByUser" => new StartupRegistrationResult(
            StartupRegistrationStatus.RequiresUserAction,
            "Startup was disabled in Windows Settings or Task Manager. Re-enable Crosio there."),
        "DisabledByPolicy" => new StartupRegistrationResult(
            StartupRegistrationStatus.DisabledByPolicy,
            "Your Windows administrator has disabled startup apps."),
        _ => new StartupRegistrationResult(
            StartupRegistrationStatus.Unavailable,
            $"Windows returned an unknown startup state: {state}."),
    };

    private static bool HasPackageIdentity()
    {
        try
        {
            _ = Package.Current.Id.Name;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
