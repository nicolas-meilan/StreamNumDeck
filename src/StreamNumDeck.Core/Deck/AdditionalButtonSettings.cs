namespace StreamNumDeck.Core.Deck;

public sealed record AdditionalButtonSettings(int Slot, uint? VirtualKeyCode, KeyAssignment Assignment)
{
    public const int Count = 8;

    public AdditionalButtonSettings WithKey(uint? virtualKeyCode) => this with { VirtualKeyCode = virtualKeyCode };
    public AdditionalButtonSettings WithAssignment(KeyAssignment assignment) => this with { Assignment = assignment };
}
