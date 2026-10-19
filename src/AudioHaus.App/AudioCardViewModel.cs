using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioHaus.App;

/*
 * Name: ConnectionBadge
 * Input: Destination name, session color, and routing state.
 * Process: Labels connections with both text and color.
 * Output: An accessible badge shared by source/output cards and the connection list.
 */
public sealed record ConnectionBadge(string Name, Brush Color);

/*
 * Name: AudioCardViewModel
 * Input: Discovered identity, selection/level callbacks, and native telemetry.
 * Process: Shares master controls, confirmation tracking, meters, and selection styling.
 * Output: A selectable card independent of whether a native routing resource exists.
 */
public abstract class AudioCardViewModel : ObservableObject
{
    private readonly Action<AudioCardViewModel> changed;
    private bool selected, managed, muted;
    private double volume = 100, level;
    private string name;
    private string state = "Not connected", error = string.Empty, levelText = "No routed audio";
    private Brush connectionBrush = new SolidColorBrush(Color.FromRgb(52, 59, 64));
    public string Name { get => name; internal set => SetProperty(ref name, value); }
    public abstract string AutomationKey { get; }
    public ObservableCollection<ConnectionBadge> Badges { get; } = new();
    public string ConnectionSummary => Badges.Count == 0 ? "Not connected" : string.Join("; ", Badges.Select(item => item.Name + " · " + item.Color));
    public ICommand SelectCommand { get; }
    public bool IsSelected { get => selected; internal set => SetProperty(ref selected, value); }
    public bool IsManaged { get => managed; internal set => SetProperty(ref managed, value); }
    public double VolumePercent
    {
        get => volume;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 100) { OnPropertyChanged(nameof(VolumePercent)); return; }
            if (SetProperty(ref volume, value)) { ++Revision; changed(this); }
        }
    }
    public bool Muted { get => muted; set { if (SetProperty(ref muted, value)) { ++Revision; changed(this); } } }
    public double Level { get => level; internal set => SetProperty(ref level, value); }
    public string LevelText { get => levelText; internal set => SetProperty(ref levelText, value); }
    public string StateText { get => state; internal set => SetProperty(ref state, value); }
    public string ErrorText { get => error; internal set { if (SetProperty(ref error, value)) { OnPropertyChanged(nameof(HasError)); } } }
    public bool HasError => ErrorText.Length > 0;
    public Brush ConnectionBrush { get => connectionBrush; private set => SetProperty(ref connectionBrush, value); }
    internal long Revision { get; private set; }
    internal bool ApplyingLevel { get; set; }
    internal double ConfirmedVolume { get; set; } = 100;
    internal bool ConfirmedMuted { get; set; }

    /*
     * Name: ControlChanged
     * Input: A changed derived-card control.
     * Process: Advances the same coalesced control revision as volume/mute.
     * Output: One latest-target notification to the owning view model.
     */
    protected void ControlChanged() { ++Revision; changed(this); }

    /*
     * Name: AudioCardViewModel
     * Input: Friendly name and selection/control callbacks.
     * Process: Initializes unity master controls and selection command.
     * Output: Unmanaged card with no native resources.
     */
    protected AudioCardViewModel(string name, Action<AudioCardViewModel> select, Action<AudioCardViewModel> changed)
    {
        this.name = name; this.changed = changed;
        SelectCommand = new AsyncCommand(() => { select(this); return System.Threading.Tasks.Task.CompletedTask; }, () => true, error => ErrorText = error.Message);
    }

    /*
     * Name: SetConnections
     * Input: Current destination badges and optional stable device color.
     * Process: Reuses equivalent badges and forms a segmented border for fan-out.
     * Output: Matching highlights without resetting selected cards or master controls.
     */
    internal void SetConnections(ConnectionBadge[] badges, Brush? deviceColor = null)
    {
        if (Badges.SequenceEqual(badges)) { return; }
        Badges.Clear(); foreach (ConnectionBadge badge in badges) { Badges.Add(badge); }
        OnPropertyChanged(nameof(ConnectionSummary));
        if (badges.Length == 0) { ConnectionBrush = new SolidColorBrush(Color.FromRgb(52, 59, 64)); return; }
        if (deviceColor is not null) { ConnectionBrush = deviceColor; return; }
        if (badges.Length == 1) { ConnectionBrush = badges[0].Color; return; }
        var gradient = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
        for (int index = 0; index < badges.Length; ++index)
        {
            Color color = ((SolidColorBrush)badges[index].Color).Color;
            gradient.GradientStops.Add(new GradientStop(color, (double)index / badges.Length));
            gradient.GradientStops.Add(new GradientStop(color, (double)(index + 1) / badges.Length));
        }
        gradient.Freeze(); ConnectionBrush = gradient;
    }

    /*
     * Name: RollbackLevel
     * Input: Last acknowledged master target.
     * Process: Corrects bindings without generating another native mutation.
     * Output: Controls reflect the known target after a failed update.
     */
    internal void RollbackLevel()
    {
        SetProperty(ref volume, ConfirmedVolume, nameof(VolumePercent));
        SetProperty(ref muted, ConfirmedMuted, nameof(Muted));
    }
}
