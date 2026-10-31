namespace StreamNumDeck.Core.Input;

public sealed class KeyboardDeviceCorrelationObservedEventArgs(
    uint virtualKeyCode,
    uint scanCode,
    bool isExtended,
    string selectedDeviceId,
    string? matchedDeviceId) : EventArgs
{
    public uint VirtualKeyCode { get; } = virtualKeyCode;

    public uint ScanCode { get; } = scanCode;

    public bool IsExtended { get; } = isExtended;

    public string SelectedDeviceId { get; } = Guard.NotNull(selectedDeviceId, nameof(selectedDeviceId));

    public string? MatchedDeviceId { get; } = matchedDeviceId;
}
