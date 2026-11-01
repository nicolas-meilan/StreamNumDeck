namespace StreamNumDeck.Core.Input;

public sealed class RawKeyboardInputObservedEventArgs(
    string deviceId,
    uint virtualKeyCode,
    uint scanCode,
    bool isExtended,
    bool isSelectedDevice,
    bool isKeyDown = true,
    CapturedKeyPress? capturedKeyPress = null,
    bool isRepeat = false) : EventArgs
{
    public string DeviceId { get; } = Guard.NotNull(deviceId, nameof(deviceId));

    public uint VirtualKeyCode { get; } = virtualKeyCode;

    public uint ScanCode { get; } = scanCode;

    public bool IsExtended { get; } = isExtended;

    public bool IsSelectedDevice { get; } = isSelectedDevice;

    public bool IsKeyDown { get; } = isKeyDown;

    public CapturedKeyPress? CapturedKeyPress { get; } = capturedKeyPress;

    public bool IsRepeat { get; } = isRepeat;
}
