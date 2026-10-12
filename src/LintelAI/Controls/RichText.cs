using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LintelAI.Controls;

/// <summary>
/// Selectable text that understands the small slice of Markdown chat replies actually use:
/// <c>**bold**</c>, <c>`inline code`</c>, <c>#</c> headings and <c>-</c> bullets.
/// </summary>
public partial class RichText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<RichText, string?>(nameof(Markdown));

    private static readonly Regex InlineToken = new(@"`([^`\n]+)`|\*\*([^*\n]+)\*\*", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^(\s*)[-*]\s+", RegexOptions.Compiled);

    static RichText()
    {
        MarkdownProperty.Changed.AddClassHandler<RichText>((control, _) => control.Rebuild());
    }

    public RichText()
    {
        TextWrapping = TextWrapping.Wrap;
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Rebuild()
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();

        var text = Markdown;
        if (string.IsNullOrEmpty(text)) return;

        var mono = FindResource("MonoFont") as FontFamily ?? FontFamily.Default;
        var codeBackground = TryFindBrush("SurfaceHoverBrush");

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) Inlines.Add(new LineBreak());

            var line = lines[i];
            var bold = false;

            if (Heading.IsMatch(line))
            {
                line = Heading.Replace(line, "");
                bold = true;
            }
            else if (Bullet.Match(line) is { Success: true } bullet)
            {
                line = bullet.Groups[1].Value + "•  " + line[bullet.Length..];
            }

            AddInline(line, bold, mono, codeBackground);
        }
    }

    private void AddInline(string line, bool forceBold, FontFamily mono, IBrush? codeBackground)
    {
        var position = 0;
        foreach (Match match in InlineToken.Matches(line))
        {
            if (match.Index > position)
                Inlines!.Add(new Run(line[position..match.Index]) { FontWeight = forceBold ? FontWeight.SemiBold : FontWeight.Normal });

            if (match.Groups[1].Success)
            {
                Inlines!.Add(new Run(match.Groups[1].Value)
                {
                    FontFamily = mono,
                    FontSize = FontSize - 1,
                    Background = codeBackground
                });
            }
            else
            {
                Inlines!.Add(new Run(match.Groups[2].Value) { FontWeight = FontWeight.SemiBold });
            }
            position = match.Index + match.Length;
        }

        if (position < line.Length)
            Inlines!.Add(new Run(line[position..]) { FontWeight = forceBold ? FontWeight.SemiBold : FontWeight.Normal });
    }

    private object? FindResource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value : null;

    private IBrush? TryFindBrush(string key) => FindResource(key) as IBrush;
}
