namespace Yolime.Core.Audio;

/// <summary>What kind of microphone a hold used: for the timing log, which can't hold its name.</summary>
public enum MicKind { Unknown, Builtin, Usb, Bluetooth }

public static class MicKinds
{
    /// <summary>From the endpoint's PnP enumerator name (DEVPKEY_Device_EnumeratorName), such as BTHENUM or USB.</summary>
    public static MicKind FromEnumerator(string? enumerator) => enumerator?.ToUpperInvariant() switch
    {
        "BTHENUM" or "BTHHFENUM" or "BTHLEDEVICE" => MicKind.Bluetooth,
        "USB" => MicKind.Usb,
        // The motherboard's codec, Intel's smart sound and laptop array mics.
        "HDAUDIO" or "INTELAUDIO" or "ACPI" => MicKind.Builtin,
        _ => MicKind.Unknown,
    };
}
