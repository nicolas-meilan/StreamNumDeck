using StreamNumDeck.Core.Deck;

namespace StreamNumDeck.Core.Input;

public interface IKeyboardCaptureService : IAsyncDisposable
{
    KeyboardCaptureState State { get; }

    bool IsNumLockOn { get; }

    KeyboardCaptureTargets CaptureTargets { get; }

    string? SelectedKeyboardDeviceId { get; }

    event EventHandler<KeyboardCaptureStateChangedEventArgs>? StateChanged;

    event EventHandler<NumLockStateChangedEventArgs>? NumLockStateChanged;

    event EventHandler<KeyboardInputObservedEventArgs>? KeyboardInputObserved;

    event EventHandler<RawKeyboardInputObservedEventArgs>? RawKeyboardInputObserved;

    event EventHandler<KeyboardCaptureErrorEventArgs>? CaptureError;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SetCaptureTargetsAsync(
        KeyboardCaptureTargets targets,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KeyboardDeviceInfo>> GetAvailableKeyboardsAsync(
        CancellationToken cancellationToken = default);

    Task SetSelectedKeyboardDeviceAsync(
        string? deviceId,
        CancellationToken cancellationToken = default);

    Task SetKeyboardRoutedKeysAsync(
        IReadOnlyCollection<DeckKey> deviceSpecificKeys,
        IReadOnlyCollection<DeckKey> anyKeyboardKeys,
        CancellationToken cancellationToken = default);

    Task SetAdditionalButtonDeviceRoutingAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task SetDeviceIdentificationKeysAsync(
        IReadOnlyCollection<DeckKey> numLockOffKeys,
        IReadOnlyCollection<DeckKey> numLockOnKeys,
        CancellationToken cancellationToken = default);

    Task SetNumLockAsync(bool isOn, CancellationToken cancellationToken = default);

    IAsyncEnumerable<CapturedKeyPress> ReadAllAsync(CancellationToken cancellationToken = default);
}
