using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoiceEngine.Client;

namespace AiCoreMonitor.ViewModels;

public sealed class ConversationLine(string role, string text) : INotifyPropertyChanged
{
    private string _text = text;
    public string Role { get; } = role;
    public string Text { get => _text; set { if (_text == value) return; _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ConversationViewModel : INotifyPropertyChanged
{
    private string _status = "VOICE SERVER OFFLINE";
    private string _partialTranscript = "";
    private bool _isActive;
    private bool _isMuted;
    private string? _selectedModel;
    private VoiceChoice? _selectedVoice;
    private VoiceChoice? _selectedProfile;
    private string _context = "auto";
    private ConversationLine? _activeAssistantLine;

    public ObservableCollection<ConversationLine> Transcript { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<VoiceChoice> Voices { get; } = [];
    public ObservableCollection<VoiceChoice> Profiles { get; } = [];
    public string[] ContextChoices { get; } = ["auto", "2048", "4096", "8192", "16384", "32768", "65536"];

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string PartialTranscript { get => _partialTranscript; private set => Set(ref _partialTranscript, value); }
    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }
    public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }
    public string? SelectedModel { get => _selectedModel; set => Set(ref _selectedModel, value); }
    public VoiceChoice? SelectedVoice { get => _selectedVoice; set => Set(ref _selectedVoice, value); }
    public VoiceChoice? SelectedProfile { get => _selectedProfile; set => Set(ref _selectedProfile, value); }
    public string Context { get => _context; set => Set(ref _context, value); }

    public void Starting()
    {
        Transcript.Clear();
        Models.Clear();
        Voices.Clear();
        Profiles.Clear();
        _activeAssistantLine = null;
        PartialTranscript = "";
        IsActive = false;
        Status = "CONNECTING";
    }

    public void Stopped()
    {
        IsActive = false;
        IsMuted = false;
        PartialTranscript = "";
        Status = "VOICE SERVER OFFLINE";
    }

    public void Failed(string message)
    {
        IsActive = false;
        Status = $"ERROR · {message}";
    }

    public void Apply(ConversationEvent item)
    {
        switch (item)
        {
            case SessionReadyEvent ready:
                IsActive = true;
                Status = "LISTENING";
                if (!Models.Contains(ready.Model)) Models.Add(ready.Model);
                SelectedModel = ready.Model;
                Context = ready.ContextMode == "auto" ? "auto" : ready.ContextTokens?.ToString() ?? "auto";
                SelectedVoice = new VoiceChoice(ready.Voice, ready.Voice);
                SelectedProfile = new VoiceChoice(ready.Profile, ready.Profile, ready.TtsBackend);
                break;
            case SpeechEvent { Type: "speech.started" }:
                Status = "LISTENING · SPEECH";
                break;
            case TranscriptEvent { Type: "transcript.partial" } partial:
                PartialTranscript = partial.Text;
                break;
            case TranscriptEvent { Type: "transcript.final" } final:
                PartialTranscript = "";
                AddLine(new ConversationLine("YOU", final.Text));
                _activeAssistantLine = null;
                break;
            case ResponseStateEvent { Type: "response.started" }:
                Status = "THINKING";
                _activeAssistantLine = null;
                break;
            case ResponseTextEvent delta:
                _activeAssistantLine ??= AddLine(new ConversationLine("ASSISTANT", ""));
                _activeAssistantLine.Text += delta.Text;
                break;
            case ResponseStateEvent { Type: "response.audio.started" }:
                Status = "SPEAKING";
                break;
            case ResponseStateEvent { Type: "response.completed" }:
                Status = "LISTENING";
                _activeAssistantLine = null;
                break;
            case ResponseStateEvent { Type: "response.cancelled" }:
                Status = "LISTENING · STOPPED";
                _activeAssistantLine = null;
                break;
            case ModelListEvent models:
                var availableModels = models.Models.ToList();
                if (!string.IsNullOrWhiteSpace(models.Current) && !availableModels.Contains(models.Current, StringComparer.Ordinal))
                    availableModels.Insert(0, models.Current);
                Replace(Models, availableModels);
                SelectedModel = models.Current;
                break;
            case VoiceListEvent voices:
                Replace(Voices, voices.Voices);
                SelectedVoice = Voices.FirstOrDefault(item => item.Id == voices.Current);
                break;
            case ProfileListEvent profiles:
                Replace(Profiles, profiles.Profiles);
                SelectedProfile = Profiles.FirstOrDefault(item => item.Id == profiles.Current);
                break;
            case ContextInfoEvent context:
                Context = context.ContextMode == "auto" ? "auto" : context.ContextTokens?.ToString() ?? Context;
                break;
            case SelectionEvent selection:
                ApplySelection(selection);
                break;
            case VoiceErrorEvent error:
                Status = $"ERROR · {error.Message}";
                break;
        }
    }

    private void ApplySelection(SelectionEvent selection)
    {
        if (selection.Type.EndsWith("loading", StringComparison.Ordinal)) Status = "LOADING MODEL";
        else if (selection.Type == "model.selected") { SelectedModel = selection.Value; Status = "LISTENING"; }
        else if (selection.Type == "voice.selected") SelectedVoice = Voices.FirstOrDefault(item => item.Id == selection.Value) ?? new VoiceChoice(selection.Value ?? "", selection.Value ?? "");
        else if (selection.Type == "profile.selected") SelectedProfile = Profiles.FirstOrDefault(item => item.Id == selection.Value) ?? new VoiceChoice(selection.Value ?? "", selection.Value ?? "", selection.Backend);
        else if (selection.Type == "context.selected") { Context = selection.Value ?? Context; Status = "LISTENING"; }
    }

    private ConversationLine AddLine(ConversationLine line)
    {
        Transcript.Add(line);
        while (Transcript.Count > 200) Transcript.RemoveAt(0);
        return line;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
