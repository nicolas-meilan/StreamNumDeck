using StreamNumDeck.Core.Deck;

namespace StreamNumDeck.Core.Input;

public readonly record struct AdditionalButtonCaptureRule(
    uint VirtualKeyCode,
    AdditionalButtonNumLockMode NumLockMode,
    bool CaptureGlobally = true);
