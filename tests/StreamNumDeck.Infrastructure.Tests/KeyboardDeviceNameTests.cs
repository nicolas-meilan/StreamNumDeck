using StreamNumDeck.Infrastructure.Input;

namespace StreamNumDeck.Infrastructure.Tests;

[TestClass]
public sealed class KeyboardDeviceNameTests
{
    [TestMethod]
    public void FormatsKeyboardNameWithEightCharacterDeviceId()
    {
        var displayName = WindowsRawInputKeyboardSource.FormatDeviceDisplayName(
            @"\\?\HID#VID_1234&PID_ABCD#12345678ABCDEF");

        Assert.AreEqual("HID Keyboard (12345678...)", displayName);
    }

    [TestMethod]
    public void FormatsKeyboardNameWithoutTruncatingShortDeviceId()
    {
        var displayName = WindowsRawInputKeyboardSource.FormatDeviceDisplayName(
            @"\\?\HID#VID_1234&PID_ABCD#ABC123");

        Assert.AreEqual("HID Keyboard (ABC123)", displayName);
    }
}
