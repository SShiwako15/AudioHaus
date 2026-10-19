using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace AudioHaus.App;

/*
 * Name: MainWindow
 * Input: User selections and observable routing state.
 * Process: Presents active sources/devices and delegates routing to its view model.
 * Output: An application-router window with orderly asynchronous restoration.
 */
public partial class MainWindow : Window
{
    private readonly MainViewModel view_model = new();
    private bool initialized, shutting_down, allow_close;
    private IInputElement? previous_focus;

    /*
     * Name: MainWindow
     * Input: Current Windows work area.
     * Process: Creates bindings and clamps the initial window to the available desktop.
     * Output: Router workspace ready for asynchronous discovery.
     */
    public MainWindow()
    {
        InitializeComponent();
        Rect work_area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, work_area.Width); MinHeight = Math.Min(MinHeight, work_area.Height);
        Width = Math.Min(Width, work_area.Width); Height = Math.Min(Height, work_area.Height);
        DataContext = view_model;
    }

    /*
     * Name: Window_Loaded
     * Input: Initial window load.
     * Process: Starts asynchronous discovery exactly once.
     * Output: Active cards or a visible initialization error.
     */
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (initialized) { return; }
        initialized = true;
        try { await view_model.InitializeAsync(); }
        catch (Exception error) { ShowWindowError(error.Message); }
    }

    /*
     * Name: Window_Closing
     * Input: A close request.
     * Process: Defers closure until routing is stopped and app-output restoration finishes.
     * Output: Closed window; cleanup failures are reported before final exit.
     */
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allow_close) { return; }
        e.Cancel = true;
        if (shutting_down) { return; }
        shutting_down = true; Workspace.IsEnabled = false; SetupOverlay.IsEnabled = false;
        try { await view_model.ShutdownAsync(); }
        catch (Exception error) { MessageBox.Show(this, "AudioHaus could not finish routing cleanup.\n\n" + error.Message, "AudioHaus", MessageBoxButton.OK, MessageBoxImage.Error); }
        allow_close = true; Close();
    }

    /*
     * Name: Card_MouseLeftButtonDown
     * Input: Any click inside an available card.
     * Process: Selects its source/device while leaving slider and mute interactions intact.
     * Output: Updated Connect/Disconnect selection without implicit audio changes.
     */
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (sender is FrameworkElement { DataContext: AudioCardViewModel card }) { view_model.SelectCard(card); } }

    /*
     * Name: Setup_Click / CloseSetup_Click / Window_PreviewKeyDown
     * Input: Settings actions or Escape.
     * Process: Opens/closes a focus-contained settings overlay.
     * Output: Restored focus on return to the workspace.
     */
    private void Setup_Click(object sender, RoutedEventArgs e)
    { previous_focus = Keyboard.FocusedElement; SetupOverlay.Visibility = Visibility.Visible; Workspace.IsEnabled = false; CloseSetupButton.Focus(); }
    /*
     * Name: CloseSetup_Click
     * Input: Settings close event.
     * Process: Returns to the workspace through the shared overlay cleanup.
     * Output: Prior keyboard focus restored.
     */
    private void CloseSetup_Click(object sender, RoutedEventArgs e) => CloseSetup();
    /*
     * Name: Window_PreviewKeyDown
     * Input: Window keyboard event.
     * Process: Handles Escape while Settings is open.
     * Output: Settings dismissed without altering routes.
     */
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape && SetupOverlay.Visibility == Visibility.Visible) { CloseSetup(); e.Handled = true; } }
    /*
     * Name: CloseSetup
     * Input: Prior keyboard focus and shutdown state.
     * Process: Hides Settings and restores workspace availability.
     * Output: Focus returns to its previous control.
     */
    private void CloseSetup()
    { SetupOverlay.Visibility = Visibility.Collapsed; Workspace.IsEnabled = !shutting_down; if (previous_focus is not null) { Keyboard.Focus(previous_focus); } }

    /*
     * Name: SoundPanel_Click / VolumeMixer_Click / OpenWindowsSettings
     * Input: Explicit user request for Windows audio settings.
     * Process: Opens a fixed system settings command; failures stay visible.
     * Output: Sound settings, without changing app defaults itself.
     */
    private void SoundPanel_Click(object sender, RoutedEventArgs e) => OpenWindowsSettings("control.exe", "mmsys.cpl");
    /*
     * Name: VolumeMixer_Click
     * Input: Explicit Volume mixer button event.
     * Process: Launches the fixed Windows Volume mixer target.
     * Output: Windows settings or a visible launch error.
     */
    private void VolumeMixer_Click(object sender, RoutedEventArgs e) => OpenWindowsSettings("ms-settings:apps-volume", string.Empty);
    /*
     * Name: OpenWindowsSettings
     * Input: Fixed command and arguments from the settings buttons.
     * Process: Launches Windows settings and catches shell failures.
     * Output: External settings window or an error banner.
     */
    private void OpenWindowsSettings(string command, string arguments)
    {
        try { using Process? settings = Process.Start(new ProcessStartInfo(command, arguments) { UseShellExecute = true }); }
        catch (Exception error) { CloseSetup(); ShowWindowError(error.Message); }
    }

    /*
     * Name: ShowWindowError / DismissWindowError_Click
     * Input: Window-level failure or dismissal.
     * Process: Displays/clears errors outside the view-model operation boundary.
     * Output: Visible failure without losing the routing session.
     */
    private void ShowWindowError(string message) { WindowErrorText.Text = message; WindowErrorBanner.Visibility = Visibility.Visible; }
    /*
     * Name: DismissWindowError_Click
     * Input: Error dismissal event.
     * Process: Clears the window-level error banner.
     * Output: Routing session remains available.
     */
    private void DismissWindowError_Click(object sender, RoutedEventArgs e) { WindowErrorBanner.Visibility = Visibility.Collapsed; WindowErrorText.Text = string.Empty; }
}
