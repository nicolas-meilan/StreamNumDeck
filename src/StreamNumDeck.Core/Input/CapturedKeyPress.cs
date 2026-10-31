using StreamNumDeck.Core.Deck;

namespace StreamNumDeck.Core.Input;

public readonly record struct CapturedKeyPress(
    DeckKey Key,
    NumLockLayer Layer,
    DateTimeOffset Timestamp);

public enum KeyboardCaptureState
{
    Stopped,
    Running,
    Paused,
}

public sealed class KeyboardCaptureStateChangedEventArgs(KeyboardCaptureState state) : EventArgs
{
    public KeyboardCaptureState State { get; } = state;
}

public sealed class NumLockStateChangedEventArgs(bool isOn) : EventArgs
{
    public bool IsOn { get; } = isOn;
}

public sealed class KeyboardInputObservedEventArgs(
    uint virtualKeyCode,
    uint scanCode,
    bool isExtended) : EventArgs
{
    public uint VirtualKeyCode { get; } = virtualKeyCode;

    public uint ScanCode { get; } = scanCode;

    public bool IsExtended { get; } = isExtended;
}

public sealed class KeyboardCaptureErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = Guard.NotNull(exception, nameof(exception));
}
