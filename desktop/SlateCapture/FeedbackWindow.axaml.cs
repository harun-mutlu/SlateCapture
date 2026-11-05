using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using SlateCapture.Net;
using static SlateCapture.Strings;

namespace SlateCapture;

public partial class FeedbackWindow : Window
{
    readonly Hello? _hello;
    readonly StreamState? _state;
    FeedbackKind _kind = FeedbackKind.Bug;

    public FeedbackWindow() : this(null, null)
    {
    }

    public FeedbackWindow(Hello? hello, StreamState? state)
    {
        InitializeComponent();
        _hello = hello;
        _state = state;

        Title = T("feedback.title");
        HeaderText.Text = T("feedback.title");
        SubtitleText.Text = T("feedback.subtitle");
        KindTitle.Text = T("feedback.kind");
        BugButton.Content = T("feedback.bug");
        IdeaButton.Content = T("feedback.idea");
        OtherButton.Content = T("feedback.other");
        MessageTitle.Text = T("feedback.message");
        MessageBox.Watermark = T("feedback.placeholder");
        EmailTitle.Text = T("feedback.email");
        DetailsLabel.Text = T("feedback.details");
        DetailsHint.Text = T("feedback.details.hint");
        CancelButton.Content = T("feedback.cancel");
        SendButton.Content = T("feedback.send");
        OkButton.Content = T("feedback.ok");

        MessageBox.TextChanged += (_, _) =>
        {
            ErrorText.Text = "";
            UpdateCount();
        };
        UpdateCount();
        Opened += (_, _) => MessageBox.Focus();
    }

    void UpdateCount() => CountText.Text = $"{MessageBox.Text?.Length ?? 0}/{Feedback.MaxLength}";

    void OnKindClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;
        _kind = (FeedbackKind)int.Parse((string)button.Tag!);
        BugButton.IsChecked = button == BugButton;
        IdeaButton.IsChecked = button == IdeaButton;
        OtherButton.IsChecked = button == OtherButton;
    }

    void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        var message = MessageBox.Text?.Trim() ?? "";
        if (message.Length < 3)
        {
            ErrorText.Text = T("feedback.empty");
            return;
        }
        if (!Feedback.CanSend(out var wait))
        {
            ErrorText.Text = T("feedback.wait", Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes)));
            return;
        }

        SendButton.IsEnabled = false;
        SendButton.Content = T("feedback.sending");

        var technical = DetailsCheck.IsChecked == true;
        var email = EmailBox.Text?.Trim();
        var item = new FeedbackItem(
            _kind,
            message,
            string.IsNullOrEmpty(email) ? null : email,
            Details(technical),
            technical ? Feedback.RecentEvents() : null,
            technical,
            LanguageName,
            DateTime.UtcNow);

        var result = await Feedback.Submit(item);
        if (result == FeedbackResult.Failed)
        {
            ErrorText.Text = T("feedback.failed");
            SendButton.IsEnabled = true;
            SendButton.Content = T("feedback.send");
            return;
        }

        var key = result == FeedbackResult.Sent ? "sent" : "queued";
        DoneTitle.Text = T($"feedback.{key}.title");
        DoneText.Text = T($"feedback.{key}.text");
        FormPanel.IsVisible = false;
        DonePanel.IsVisible = true;
        OkButton.Focus();
    }

    Dictionary<string, string> Details(bool technical)
    {
        var details = new Dictionary<string, string>
        {
            ["Sürüm"] = Feedback.AppVersion,
            ["Bilgisayar"] = Feedback.OsName,
        };
        if (_hello != null)
        {
            details["Tablet"] = _hello.Name;
            if (technical) details["Tablet sistemi"] = $"{_hello.Os} (API {_hello.Sdk})";
        }
        if (technical && _state is { Streaming: true } s)
            details["Yayın"] = $"{s.Width}×{s.Height} · {s.Fps} FPS" + (s.Audio ? " · ses" : "");
        return details;
    }
}
