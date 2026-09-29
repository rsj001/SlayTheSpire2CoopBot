using Godot;

namespace CoopBot.UI;

internal partial class CoopBotOverlayRenderer : CanvasLayer
{
    private PanelContainer? _panel;
    private Label? _title;
    private Label? _session;
    private Label? _status;
    private Label? _score;
    private Label? _current;
    private Label? _detail;
    private VBoxContainer? _actors;
    private VBoxContainer? _route;
    private HBoxContainer? _hostControls;
    private HBoxContainer? _clientControls;
    private Button? _replanButton;
    private Button? _cancelButton;
    private Button? _executeButton;
    private Button? _hostPauseButton;
    private Button? _allowButton;
    private Button? _rejectButton;
    private Button? _clientPauseButton;
    private Button? _observeButton;
    private Button? _suggestButton;
    private Button? _confirmButton;
    private Button? _autoButton;

    internal event Action? ReplanRequested;
    internal event Action? CancelSearchRequested;
    internal event Action? ExecuteNextRequested;
    internal event Action? PauseRequested;
    internal event Action? AllowOnceRequested;
    internal event Action? RejectRequested;
    internal event Action<CoopBot.Protocol.CoopAutomationMode>? AutomationModeRequested;

    public override void _Ready()
    {
        Layer = 90;
        _panel = new PanelContainer
        {
            Position = new Vector2(24, 120),
            CustomMinimumSize = new Vector2(440, 280),
            Visible = false,
        };
        AddChild(_panel);
        VBoxContainer body = new();
        _panel.AddChild(body);
        _title = AddLabel(body, string.Empty);
        _session = AddLabel(body, string.Empty);
        _status = AddLabel(body, string.Empty);
        _score = AddLabel(body, string.Empty);
        _current = AddLabel(body, string.Empty);
        _detail = AddLabel(body, string.Empty);
        body.AddChild(new HSeparator());
        _actors = new VBoxContainer();
        body.AddChild(_actors);
        body.AddChild(new HSeparator());
        ScrollContainer scroll = new() { CustomMinimumSize = new Vector2(420, 120) };
        body.AddChild(scroll);
        _route = new VBoxContainer();
        scroll.AddChild(_route);
        _hostControls = new HBoxContainer();
        body.AddChild(_hostControls);
        _replanButton = AddButton(_hostControls, () => ReplanRequested?.Invoke());
        _cancelButton = AddButton(_hostControls, () => CancelSearchRequested?.Invoke());
        _executeButton = AddButton(_hostControls, () => ExecuteNextRequested?.Invoke());
        _hostPauseButton = AddButton(_hostControls, () => PauseRequested?.Invoke());
        _observeButton = AddButton(_hostControls, () =>
            AutomationModeRequested?.Invoke(CoopBot.Protocol.CoopAutomationMode.Observe));
        _suggestButton = AddButton(_hostControls, () =>
            AutomationModeRequested?.Invoke(CoopBot.Protocol.CoopAutomationMode.Suggest));
        _confirmButton = AddButton(_hostControls, () =>
            AutomationModeRequested?.Invoke(CoopBot.Protocol.CoopAutomationMode.ConfirmEach));
        _autoButton = AddButton(_hostControls, () =>
            AutomationModeRequested?.Invoke(CoopBot.Protocol.CoopAutomationMode.Auto));
        _clientControls = new HBoxContainer();
        body.AddChild(_clientControls);
        _allowButton = AddButton(_clientControls, () => AllowOnceRequested?.Invoke());
        _rejectButton = AddButton(_clientControls, () => RejectRequested?.Invoke());
        _clientPauseButton = AddButton(_clientControls, () => PauseRequested?.Invoke());
    }

    internal void Render(CoopUiSnapshot snapshot)
    {
        if (_panel is null || _title is null || _session is null || _status is null
            || _score is null || _current is null || _detail is null || _actors is null || _route is null
            || _hostControls is null || _clientControls is null || _replanButton is null
            || _cancelButton is null || _executeButton is null || _hostPauseButton is null
            || _allowButton is null || _rejectButton is null || _clientPauseButton is null
            || _observeButton is null || _suggestButton is null || _confirmButton is null
            || _autoButton is null)
        {
            throw new InvalidOperationException("CoopBot overlay is not ready.");
        }
        _title.Text = snapshot.Title;
        _session.Text = snapshot.SessionLine;
        _status.Text = snapshot.StatusLine;
        _score.Text = snapshot.ScoreLine;
        _current.Text = snapshot.CurrentActionLine;
        _detail.Text = snapshot.DetailLine;
        ReplaceLabels(_actors, snapshot.Actors.Select(static actor => actor.Text));
        ReplaceLabels(_route, snapshot.Route.Select(step => step.IsCurrent ? $"▶ {step.Text}" : step.Text));
        _hostControls.Visible = snapshot.ShowHostControls;
        _clientControls.Visible = snapshot.ShowClientControls;
        _replanButton.Text = snapshot.ReplanLabel;
        _cancelButton.Text = snapshot.CancelSearchLabel;
        _executeButton.Text = snapshot.ExecuteNextLabel;
        _hostPauseButton.Text = snapshot.PauseLabel;
        _allowButton.Text = snapshot.AllowOnceLabel;
        _rejectButton.Text = snapshot.RejectLabel;
        _clientPauseButton.Text = snapshot.PauseLabel;
        _observeButton.Text = snapshot.ObserveLabel;
        _suggestButton.Text = snapshot.SuggestLabel;
        _confirmButton.Text = snapshot.ConfirmEachLabel;
        _autoButton.Text = snapshot.AutoLabel;
        _panel.Visible = true;
    }

    internal void HideOverlay()
    {
        if (_panel is not null)
            _panel.Visible = false;
    }

    private static Label AddLabel(Node parent, string text)
    {
        Label label = new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        parent.AddChild(label);
        return label;
    }

    private static Button AddButton(Node parent, Action callback)
    {
        Button button = new();
        button.Pressed += callback;
        parent.AddChild(button);
        return button;
    }

    private static void ReplaceLabels(Node parent, IEnumerable<string> values)
    {
        foreach (Node child in parent.GetChildren())
            child.QueueFree();
        foreach (string value in values)
            AddLabel(parent, value);
    }
}
