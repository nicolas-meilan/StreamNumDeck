using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.Generic;
using StreamNumDeck.Core.Deck;

namespace StreamNumDeck.Wpf;

internal sealed class AdditionalKeyCaptureWindow : Window
{
    private readonly List<uint> virtualKeySequence = [];
    private readonly TextBlock instruction;
    private readonly TextBlock capturedSequence;

    public IReadOnlyList<uint> VirtualKeySequence => virtualKeySequence;

    public AdditionalKeyCaptureWindow()
    {
        Title = "Assign a key";
        Width = 420;
        Height = 210;
        Focusable = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.DimGray;
        Foreground = System.Windows.Media.Brushes.White;

        instruction = new TextBlock
        {
            Text = $"Press up to {AdditionalButtonSettings.MaximumKeySequenceLength} keys, then click Done (Esc cancels).",
            TextAlignment = TextAlignment.Center,
            FontSize = 15,
            Margin = new Thickness(12),
        };
        capturedSequence = new TextBlock
        {
            Text = "No keys captured",
            TextAlignment = TextAlignment.Center,
            FontSize = 16,
            Margin = new Thickness(12),
        };
        var doneButton = new Button
        {
            Content = "Done",
            MinWidth = 80,
            Margin = new Thickness(6),
            IsEnabled = false,
        };
        doneButton.Click += (_, _) =>
        {
            if (virtualKeySequence.Count > 0)
            {
                DialogResult = true;
            }
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 80,
            Margin = new Thickness(6),
        };
        cancelButton.Click += (_, _) => DialogResult = false;
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Children = { doneButton, cancelButton },
        };
        Content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { instruction, capturedSequence, buttons },
        };
        PreviewKeyDown += (sender, e) => CaptureKey(sender, e, doneButton);
        Loaded += (_, _) => Keyboard.Focus(this);
    }

    private void CaptureKey(object? sender, System.Windows.Input.KeyEventArgs e, Button doneButton)
    {
        e.Handled = true;
        if (e.Key == Key.Escape) { DialogResult = false; return; }
        if (e.IsRepeat || virtualKeySequence.Count >= AdditionalButtonSettings.MaximumKeySequenceLength)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var code = KeyInterop.VirtualKeyFromKey(key);
        if (code <= 0) return;
        virtualKeySequence.Add((uint)code);
        capturedSequence.Text = string.Join(
            " + ",
            virtualKeySequence.ConvertAll(value => KeyInterop.KeyFromVirtualKey((int)value).ToString()));
        doneButton.IsEnabled = true;
        if (virtualKeySequence.Count == AdditionalButtonSettings.MaximumKeySequenceLength)
        {
            instruction.Text = "Maximum sequence length reached. Click Done to assign it.";
        }
    }
}
