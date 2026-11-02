using System.Windows.Input;

namespace StreamNumDeck.Wpf.Presentation;

internal static class AdditionalButtonKeyPresentation
{
    private const uint BackspaceVirtualKey = 0x08;

    public static uint GetVirtualKey(Key key) => key is Key.Back
        ? BackspaceVirtualKey
        : (uint)Math.Max(KeyInterop.VirtualKeyFromKey(key), 0);

    public static string GetLabel(uint virtualKeyCode) => virtualKeyCode is BackspaceVirtualKey
        ? "Backspace"
        : KeyInterop.KeyFromVirtualKey((int)virtualKeyCode).ToString();
}
