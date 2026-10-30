using Yolime.Core.Audio;

namespace Yolime.Core.Tests.Audio;

public class MicKindsTests
{
    [Theory]
    [InlineData("BTHENUM", MicKind.Bluetooth)]
    [InlineData("BTHHFENUM", MicKind.Bluetooth)]
    [InlineData("BTHLEDEVICE", MicKind.Bluetooth)]
    [InlineData("USB", MicKind.Usb)]
    [InlineData("usb", MicKind.Usb)]
    [InlineData("HDAUDIO", MicKind.Builtin)]
    [InlineData("INTELAUDIO", MicKind.Builtin)]
    [InlineData("ACPI", MicKind.Builtin)]
    [InlineData("SWD", MicKind.Unknown)]
    [InlineData("", MicKind.Unknown)]
    [InlineData(null, MicKind.Unknown)]
    public void FromTheEnumeratorName(string? enumerator, MicKind kind) => Assert.Equal(kind, MicKinds.FromEnumerator(enumerator));
}
