using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WolfSpeak;

/// <summary>Raises <see cref="Changed"/> when audio devices are plugged/unplugged or the Windows default changes.</summary>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    readonly MMDeviceEnumerator enumerator = new();

    /// <summary>Raised on a COM thread; marshal to the UI before touching anything.</summary>
    public event Action? Changed;

    public DeviceWatcher() => enumerator.RegisterEndpointNotificationCallback(this);

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed?.Invoke();
    public void OnDeviceAdded(string pwstrDeviceId) => Changed?.Invoke();
    public void OnDeviceRemoved(string deviceId) => Changed?.Invoke();

    // Any role: SteelSeries Sonar (and Windows' per-app device settings) re-route an app by changing
    // its own default device, which can arrive as a console, multimedia or communications change.
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Changed?.Invoke();

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        enumerator.Dispose();
    }
}
