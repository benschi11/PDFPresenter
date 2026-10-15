using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PdfPresenter;

public enum SaveChoice { Save, Discard, Cancel }

/// <summary>Kleine Rückfrage „Änderungen speichern?“ mit Speichern / Verwerfen / Abbrechen.</summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string text)
    {
        Title = "Ungespeicherte Annotationen";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button Make(string label, SaveChoice choice, bool isDefault = false)
        {
            var b = new Button { Content = label, IsDefault = isDefault, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => Close(choice);
            return b;
        }
        var cancel = Make("Abbrechen", SaveChoice.Cancel);
        cancel.IsCancel = true;
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = text, MaxWidth = 380, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { Make("Speichern", SaveChoice.Save, isDefault: true), Make("Verwerfen", SaveChoice.Discard), cancel },
                },
            },
        };
    }

    public static async Task<SaveChoice> AskAsync(Window owner, string text) =>
        await new ConfirmDialog(text).ShowDialog<SaveChoice?>(owner) ?? SaveChoice.Cancel;
}
