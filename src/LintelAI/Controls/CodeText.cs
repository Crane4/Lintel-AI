using LintelAI.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LintelAI.Controls;

/// <summary>Selectable, syntax-coloured code. Colours come from the Syn*Brush theme resources.</summary>
public class CodeText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<CodeText, string?>(nameof(Code));

    public static readonly StyledProperty<string?> LanguageProperty =
        AvaloniaProperty.Register<CodeText, string?>(nameof(Language));

    static CodeText()
    {
        CodeProperty.Changed.AddClassHandler<CodeText>((control, _) => control.Rebuild());
        LanguageProperty.Changed.AddClassHandler<CodeText>((control, _) => control.Rebuild());
    }

    public CodeText()
    {
        // Colours depend on the active theme, so repaint when it flips.
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public string? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public string? Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    private void Rebuild()
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();

        var code = Code;
        if (string.IsNullOrEmpty(code)) return;

        var brushes = new Dictionary<TokenKind, IBrush?>
        {
            [TokenKind.Keyword] = Find("SynKeywordBrush"),
            [TokenKind.Control] = Find("SynControlBrush"),
            [TokenKind.Type] = Find("SynTypeBrush"),
            [TokenKind.Function] = Find("SynFunctionBrush"),
            [TokenKind.String] = Find("SynStringBrush"),
            [TokenKind.Number] = Find("SynNumberBrush"),
            [TokenKind.Comment] = Find("SynCommentBrush"),
            [TokenKind.Property] = Find("SynPropertyBrush"),
            [TokenKind.Tag] = Find("SynTagBrush"),
            [TokenKind.Attribute] = Find("SynAttributeBrush")
        };

        foreach (var token in SyntaxHighlighter.Tokenize(code, Language))
        {
            var run = new Run(token.Text);
            if (token.Kind == TokenKind.Comment) run.FontStyle = FontStyle.Italic;
            if (token.Kind != TokenKind.Plain && brushes.TryGetValue(token.Kind, out var brush) && brush is not null)
                run.Foreground = brush;
            Inlines.Add(run);
        }
    }

    private IBrush? Find(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
