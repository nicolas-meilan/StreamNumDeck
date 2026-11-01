using StreamNumDeck.Core.Actions;
using StreamNumDeck.Core.Icons;

namespace StreamNumDeck.Core.Deck;

public sealed record KeyAssignment
{
    public const int MaxLabelLength = 40;
    public const uint FirstRemappableVirtualKeyCode = 0x7C;
    public const uint LastRemappableVirtualKeyCode = 0x87;

    public KeyAssignment(string label, IconReference icon, ActionDefinition action)
    {
        Guard.NotNull(icon, nameof(icon));
        Guard.NotNull(action, nameof(action));

        if (action is NoActionDefinition)
        {
            Label = string.Empty;
            Icon = IconReference.BuiltIn("square");
            Action = action;
            return;
        }

        label = label?.Trim() ?? string.Empty;

        if (label.Length > MaxLabelLength)
        {
            throw new ArgumentOutOfRangeException(nameof(label), $"Labels cannot exceed {MaxLabelLength} characters.");
        }

        Label = label;
        Icon = icon;
        Action = action;
    }

    public string Label { get; }

    public IconReference Icon { get; }

    public ActionDefinition Action { get; }

    public bool IdentifySelectedKeyboardBeforeBlocking { get; init; }

    private uint? remappedVirtualKeyCode;

    public uint? RemappedVirtualKeyCode
    {
        get => remappedVirtualKeyCode;
        init
        {
            if (value is uint virtualKeyCode
                && (virtualKeyCode < FirstRemappableVirtualKeyCode
                    || virtualKeyCode > LastRemappableVirtualKeyCode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RemappedVirtualKeyCode),
                    value,
                    $"Remapped keys must be between F13 and F24 ({FirstRemappableVirtualKeyCode:X2}-{LastRemappableVirtualKeyCode:X2}).");
            }

            remappedVirtualKeyCode = value;
        }
    }

    public static KeyAssignment Empty { get; } = new(
        string.Empty,
        IconReference.BuiltIn("square"),
        new NoActionDefinition());
}
