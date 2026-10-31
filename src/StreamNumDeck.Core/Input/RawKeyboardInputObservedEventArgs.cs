namespace StreamNumDeck.Core.Input;

public sealed class RawKeyboardInputObservedEventArgs(
    string deviceId,
    uint virtualKeyCode,
    uint scanCode,
    bool isExtended,
    bool isSelectedDevice) : EventArgs
{
    public string DeviceId { get; } = Guard.NotNull(deviceId, nameof(deviceId));

    public uint VirtualKeyCode { get; } = virtualKeyCode;

    public uint ScanCode { get; } = scanCode;

    public bool IsExtended { get; } = isExtended;

    public bool IsSelectedDevice { get; } = isSelectedDevice;
}
