using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamNumDeck.Core.Actions;
using StreamNumDeck.Core.Deck;
using StreamNumDeck.Core.Icons;
using StreamNumDeck.Wpf.Presentation;

namespace StreamNumDeck.Wpf.ViewModels;

public sealed class AdditionalButtonTileViewModel(int slot) : ObservableObject
{
    private string title = $"Button {slot + 1}";
    private string label = "Assign a key";
    private string keyLabel = "No key assigned";
    private string glyph = DeckPresentation.GetGlyph("plus");
    private string glyphFontFamily = "Segoe Fluent Icons";
    private ImageSource? customIconSource;
    private Visibility glyphVisibility = Visibility.Visible;
    private Visibility customIconVisibility = Visibility.Collapsed;
    public int Slot { get; } = slot;
    public string Title { get => title; private set => SetProperty(ref title, value); }
    public string Label { get => label; private set => SetProperty(ref label, value); }
    public string KeyLabel { get => keyLabel; private set => SetProperty(ref keyLabel, value); }
    public string Glyph { get => glyph; private set => SetProperty(ref glyph, value); }
    public string GlyphFontFamily { get => glyphFontFamily; private set => SetProperty(ref glyphFontFamily, value); }
    public ImageSource? CustomIconSource { get => customIconSource; private set => SetProperty(ref customIconSource, value); }
    public Visibility GlyphVisibility { get => glyphVisibility; private set => SetProperty(ref glyphVisibility, value); }
    public Visibility CustomIconVisibility { get => customIconVisibility; private set => SetProperty(ref customIconVisibility, value); }

    public void Update(
        IReadOnlyList<uint> virtualKeySequence,
        KeyAssignment assignment,
        IIconAssetStore iconAssetStore)
    {
        Title = $"Button {Slot + 1}";
        KeyLabel = virtualKeySequence.Count > 0
            ? string.Join(" + ", virtualKeySequence.Select(AdditionalButtonKeyPresentation.GetLabel))
            : "No key assigned";
        Label = virtualKeySequence.Count == 0 ? "Assign a key" : assignment.Action is NoActionDefinition ? "Configure action" : assignment.Label;
        var icon = assignment.Icon;
        if (icon.Kind == IconKind.CustomAsset)
        {
            var path = iconAssetStore.ResolvePath(icon);
            CustomIconSource = File.Exists(path) ? TryLoadImage(path) : null;
            GlyphVisibility = CustomIconSource is null ? Visibility.Visible : Visibility.Collapsed;
            CustomIconVisibility = CustomIconSource is null ? Visibility.Collapsed : Visibility.Visible;
            GlyphFontFamily = "Segoe Fluent Icons";
            Glyph = DeckPresentation.GetGlyph("plus");
        }
        else if (icon.Kind == IconKind.Emoji)
        {
            CustomIconSource = EmojiCatalog.TryLoadImage(icon.Value);
            GlyphVisibility = CustomIconSource is null ? Visibility.Visible : Visibility.Collapsed;
            CustomIconVisibility = CustomIconSource is null ? Visibility.Collapsed : Visibility.Visible;
            GlyphFontFamily = "Segoe UI Emoji";
            Glyph = icon.Value;
        }
        else
        {
            CustomIconSource = null;
            CustomIconVisibility = Visibility.Collapsed;
            GlyphVisibility = string.Equals(icon.Value, "blank", StringComparison.Ordinal) ? Visibility.Collapsed : Visibility.Visible;
            GlyphFontFamily = "Segoe Fluent Icons";
            Glyph = DeckPresentation.GetGlyph(icon.Value);
        }
    }

    private static ImageSource? TryLoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
