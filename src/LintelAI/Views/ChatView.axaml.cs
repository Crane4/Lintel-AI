using LintelAI.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LintelAI.Views;

public partial class ChatView : UserControl
{
    private bool _stickToBottom = true;

    public ChatView()
    {
        InitializeComponent();

        // Enter sends, Shift+Enter inserts a newline. Tunnelling lets this run before the TextBox swallows the key.
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);

        // Follow the stream only while the reader is already at the bottom.
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0)
            {
                if (_stickToBottom) Scroller.ScrollToEnd();
            }
            else
            {
                var atBottom = Scroller.Offset.Y + Scroller.Viewport.Height >= Scroller.Extent.Height - 24;
                _stickToBottom = atBottom;
            }
        };
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (DataContext is not ChatViewModel chat) return;

        e.Handled = true;
        if (chat.SendCommand.CanExecute(null)) chat.SendCommand.Execute(null);
    }
}
