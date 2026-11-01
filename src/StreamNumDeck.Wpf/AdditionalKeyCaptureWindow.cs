using System.Windows;
using System.Windows.Input;

namespace StreamNumDeck.Wpf;

internal sealed class AdditionalKeyCaptureWindow : Window
{
    public uint VirtualKeyCode { get; private set; }

    public AdditionalKeyCaptureWindow()
    {
        Title = "Assign a key";
        Width = 380;
        Height = 160;
        Focusable = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.DimGray;
        Foreground = System.Windows.Media.Brushes.White;
        Content = new System.Windows.Controls.TextBlock
        {
            Text = "Press a key to assign (Esc cancels)",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            FontSize = 16,
        };
        PreviewKeyDown += CaptureKey;
        Loaded += (_, _) => Keyboard.Focus(this);
    }

    private void CaptureKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape) { DialogResult = false; return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var code = KeyInterop.VirtualKeyFromKey(key);
        if (code <= 0) return;
        VirtualKeyCode = (uint)code;
        DialogResult = true;
    }
}
