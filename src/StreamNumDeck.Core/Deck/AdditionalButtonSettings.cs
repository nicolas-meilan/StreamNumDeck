using System.Collections.Immutable;

namespace StreamNumDeck.Core.Deck;

public sealed record AdditionalButtonSettings(int Slot, uint? VirtualKeyCode, KeyAssignment Assignment)
{
    public const int Count = 8;
    public const int MaximumKeySequenceLength = 4;

    private ImmutableArray<uint> virtualKeySequence = ImmutableArray<uint>.Empty;

    public ImmutableArray<uint> VirtualKeySequence
    {
        get => virtualKeySequence.IsDefault ? ImmutableArray<uint>.Empty : virtualKeySequence;
        init => virtualKeySequence = value.IsDefault ? ImmutableArray<uint>.Empty : value;
    }

    public bool HasKeySequence => !VirtualKeySequence.IsDefaultOrEmpty || VirtualKeyCode.HasValue;

    public ImmutableArray<uint> GetKeySequence() =>
        !VirtualKeySequence.IsDefaultOrEmpty
            ? VirtualKeySequence
            : VirtualKeyCode is uint keyCode ? [keyCode] : [];

    public AdditionalButtonSettings WithKey(uint? virtualKeyCode) =>
        this with
        {
            VirtualKeyCode = virtualKeyCode,
            VirtualKeySequence = virtualKeyCode is uint keyCode ? [keyCode] : [],
        };

    public AdditionalButtonSettings WithKeySequence(IEnumerable<uint> virtualKeySequence)
    {
        var sequence = Guard.NotNull(virtualKeySequence, nameof(virtualKeySequence)).ToImmutableArray();
        if (sequence.Length > MaximumKeySequenceLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(virtualKeySequence),
                $"Additional button sequences cannot exceed {MaximumKeySequenceLength} keys.");
        }

        return this with
        {
            VirtualKeyCode = sequence.Length == 1 ? sequence[0] : null,
            VirtualKeySequence = sequence,
        };
    }

    public AdditionalButtonSettings WithAssignment(KeyAssignment assignment) => this with { Assignment = assignment };
}
