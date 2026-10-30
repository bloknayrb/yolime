using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Yolime.App.Audio;

/// <summary>
/// Calls back when a device appears, goes away or a new default capture device is chosen, so a mic kept ready isn't
/// left on a device Windows has moved away from (a headset that just connected, a favourite plugged back in).
/// </summary>
internal sealed class MicChangeWatcher : IDisposable
{
    private readonly MMDeviceEnumerator? _devices;
    private readonly MMDeviceNotificationClient? _client;

    public MicChangeWatcher(Action onChanged)
    {
        // Off the notification thread: Core Audio callbacks must not wait on locks, and the suspend takes one.
        void changed() => _ = Task.Run(onChanged);
        try
        {
            _devices = new MMDeviceEnumerator();
            _client = _devices.CreateNotificationClient(false);
            _client.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Capture) changed();
            };
            _client.DeviceAdded += (_, _) => changed();
            _client.DeviceRemoved += (_, _) => changed();
            _client.DeviceStateChanged += (_, _) => changed();
        }
        catch (COMException)
        {
            // Best effort: without it, a kept-ready mic still closes on its timer as usual.
            _client = null;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _devices?.Dispose();
    }
}
