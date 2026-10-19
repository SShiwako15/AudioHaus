using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace AudioHaus.DesktopChecks;

/*
 * Name: AutomaticRenderer
 * Input: Built policy-following renderer and a frequency.
 * Process: Owns one hidden application and preserves command/readiness streams.
 * Output: A test PID with bounded cleanup; user applications are not touched.
 */
internal sealed class AutomaticRenderer : IDisposable
{
    private readonly Process process;
    public int Id => process.Id;
    public AutomaticRenderer(string executable, string frequency)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(frequency); process = Process.Start(start) ?? throw new InvalidOperationException("Renderer launch failed.");
        var readiness = process.StandardOutput.ReadLineAsync();
        if (!readiness.Wait(8000) || readiness.Result?.StartsWith("READY ", StringComparison.Ordinal) != true)
        { Dispose(); throw new InvalidOperationException("Automatic renderer did not become ready."); }
        process.StandardInput.AutoFlush = true;
    }
    public void Send(string command) => process.StandardInput.WriteLine(command);
    public void Dispose()
    {
        if (!process.HasExited) { try { Send("quit"); } catch (IOException) { } if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(); } }
        process.Dispose();
    }
}

/*
 * Name: Program
 * Input: Built app; optional --router renderer/cable/two-output IDs and screenshot.
 * Process: Drives the real redesigned WPF controls with actual policy-following audio.
 * Output: Layout/control/routing evidence and normal app shutdown.
 */
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string? screenshot = null;
            if (args.Length >= 3 && args[^2] == "--screenshot") { screenshot = Path.GetFullPath(args[^1]); args = args[..^2]; }
            if (args.Length is not 1 and not 6) { Console.WriteLine("DesktopChecks <app-exe> [--router <automatic-renderer> <cable-id> <output-a> <output-b>] [--screenshot <png>]"); return 1; }
            using var app = new DesktopApplication(args[0]);
            CheckLayout(app);
            if (args.Length == 6)
            {
                if (args[1] == "--router") { CheckRouting(app, args[2], args[3], args[4], args[5], screenshot); }
                else if (args[1] is "--applications" or "--applications-outage" or "--recovery")
                { CheckApplications(app, args[2], args[3], args[4], args[5], screenshot, args[1] != "--applications", args[1] == "--recovery"); }
                else { throw new InvalidOperationException("Unknown test mode."); }
            }
            else { if (screenshot is not null) { app.Screenshot(screenshot); } app.Close(); }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    /*
     * Name: CheckLayout
     * Input: Actual router window.
     * Process: Checks orientation, removal of player controls, settings containment, and selections.
     * Output: Passed software UI checks without creating routes.
     */
    private static void CheckLayout(DesktopApplication app)
    {
        Require(app.Element("OutputsList").Current.BoundingRectangle.Left < app.Element("SourcesList").Current.BoundingRectangle.Left,
            "Output devices must be left of application sources.");
        Require(app.Find("AddFilesButton") is null && app.Find("ProcessIdInput") is null && app.Find("AddOutputButton") is null, "Old player/manual setup controls remain in the workspace.");
        Require(!app.Element("ConnectButton").Current.IsEnabled, "Connect enabled without selections.");
        app.Invoke("SettingsButton"); app.Wait(() => app.Find("CloseSettingsButton")?.Current.IsEnabled == true, "settings opened");
        Require(!app.Element("ConnectButton").Current.IsEnabled && app.Find("ParkingOutputSelector") is not null, "Settings did not contain cable configuration.");
        app.Invoke("CloseSettingsButton");
        Require(string.IsNullOrWhiteSpace(app.Text("ErrorText")), "Startup error: " + app.Text("ErrorText"));
        Console.WriteLine("PASS: left devices/right active sources, no player/manual capture controls, Settings, and empty-selection action states.");
    }

    /*
     * Name: CheckRouting
     * Input: Actual window and two owned default-policy renderers.
     * Process: Selects active cards, connects, changes masters, pauses input, and closes routing.
     * Output: UI-observed fan-out, mixing, matching colors, refresh, mute, and restoration cleanup.
     */
    private static void CheckRouting(DesktopApplication app, string renderer, string cable, string outputA, string outputB, string? screenshot)
    {
        using var first = new AutomaticRenderer(renderer, "330"); using var second = new AutomaticRenderer(renderer, "550");
        string a = SourceKey(app, first.Id), b = SourceKey(app, second.Id);
        app.Wait(() => app.Find("SelectOutput_" + outputA) is not null && app.Find("SelectOutput_" + outputB) is not null, "two active output cards");
        Require(app.Find("SelectOutput_" + cable) is null, "Reserved parking cable appeared as a playback device.");
        Connect(app, a, outputA); Signal(app, outputA, true);
        string sourceHelp = app.Element("SelectSource_" + a).Current.HelpText, outputHelp = app.Element("SelectOutput_" + outputA).Current.HelpText;
        string color = outputHelp.Split('·')[^1].Trim();
        Require(sourceHelp.Contains(color, StringComparison.Ordinal) && color.StartsWith('#'), "Source and output do not expose the same connection color.");
        Connect(app, a, outputB); Signal(app, outputA, true); Signal(app, outputB, true);
        Require(app.Element("SelectSource_" + a).Current.HelpText.Contains(';'), "Fan-out source omitted its second colored destination.");
        Console.WriteLine("PASS: select-and-connect fan-out and matching labeled connection colors.");
        Thread.Sleep(2200);
        Require(app.Text("SelectionText").Contains("AutomaticRenderer", StringComparison.Ordinal), "Automatic refresh lost the source selection.");
        app.SetRange("Volume_" + a, 50); Signal(app, outputA, true); Signal(app, outputB, true);
        app.Toggle("Mute_" + a, true); Signal(app, outputA, false); Signal(app, outputB, false); Signal(app, a, false);
        app.Toggle("Mute_" + a, false); app.Toggle("Mute_" + outputA, true); Signal(app, outputA, false); Signal(app, outputB, true); Signal(app, a, true);
        app.Toggle("Mute_" + outputA, false); app.SetRange("Volume_" + outputA, 40);
        Connect(app, b, outputA); Signal(app, outputA, true); Signal(app, outputB, true);
        if (screenshot is not null) { app.Screenshot(screenshot); }
        second.Send("pause");
        app.Wait(() => app.Find("SelectSource_" + b) is null, "paused inactive source removed automatically", 20000);
        Require(app.Dump().Contains("Waiting for source audio", StringComparison.Ordinal), "Retained inactive connection is not accessible.");
        second.Send("play"); SourceKey(app, second.Id);
        app.Invoke("SelectSource_" + a); app.Invoke("SelectOutput_" + outputA); app.Invoke("DisconnectButton");
        app.Wait(() => !app.Element("DisconnectButton").Current.IsEnabled, "selected route disconnected");
        Signal(app, outputA, true); Signal(app, outputB, true);
        Require(string.IsNullOrWhiteSpace(app.Text("ErrorText")), "Unexpected routing error: " + app.Text("ErrorText"));
        app.Close();
        Console.WriteLine("PASS: source/output volume and mute, independent two-source mixing, automatic inactive-source refresh, retained routes, disconnection, and normal close.");
    }

    private static string SourceKey(DesktopApplication app, int pid)
    {
        string? key = null;
        app.Wait(() => (key = app.Keys("SelectSource_").FirstOrDefault(item => item.StartsWith(pid + "_", StringComparison.Ordinal))) is not null, "active source PID " + pid, 20000);
        return key!;
    }

    /*
     * Name: CheckApplications
     * Input: Two user-selected, already-playing app names and intended output IDs.
     * Process: Uses only the shipped WPF controls, with longer listening phases.
     * Output: Real-app routing evidence; app closure restores output policy and leaves both apps running.
     */
    private static void CheckApplications(DesktopApplication app, string firstName, string secondName, string outputA, string outputB, string? screenshot, bool outage, bool recoveryOnly)
    {
        string? a = null, b = null;
        app.Wait(() =>
        {
            a = app.Keys("SelectSource_").FirstOrDefault(key => app.Element("SelectSource_" + key).Current.Name.Equals(firstName, StringComparison.OrdinalIgnoreCase));
            b = app.Keys("SelectSource_").FirstOrDefault(key => app.Element("SelectSource_" + key).Current.Name.Equals(secondName, StringComparison.OrdinalIgnoreCase));
            return a is not null && b is not null;
        }, "both requested applications active", 30000);
        Console.WriteLine($"User apps: {firstName} {a}; {secondName} {b}.");
        Phase("Lead-in: preparing real application routes", 10);
        Connect(app, a!, outputA); Connect(app, b!, outputB); Signal(app, outputA, true); Signal(app, outputB, true);
        if (screenshot is not null) { app.Screenshot(screenshot); }
        if (!recoveryOnly)
        {
        Phase($"{firstName} on output A; {secondName} on output B", 40);
        app.Toggle("Mute_" + a!, true); Signal(app, outputA, false); Signal(app, outputB, true);
        Phase($"Only {firstName} muted; {secondName} continues", 12);
        app.Toggle("Mute_" + a!, false); app.SetRange("Volume_" + b!, 40);
        Signal(app, outputA, true); Signal(app, outputB, true);
        Phase($"{firstName} restored; {secondName} quieter", 12);
        app.Toggle("Mute_" + outputB, true); Signal(app, outputB, false); Signal(app, outputA, true);
        Phase("Only output B muted; output A continues", 12);
        app.Toggle("Mute_" + outputB, false); app.SetRange("Volume_" + b!, 100);
        Connect(app, a!, outputB); Signal(app, outputA, true); Signal(app, outputB, true);
        Phase($"{firstName} on both outputs, mixed with {secondName} on output B", 18);
        app.Invoke("DisconnectButton");
        Phase("Back to separate application destinations", 12);
        }
        if (outage) { CheckDeviceOutage(app, outputA, outputB); }
        Require(string.IsNullOrWhiteSpace(app.Text("ErrorText")), "Real-app routing error: " + app.Text("ErrorText"));
        app.Close();
        Phase("AudioHaus closed; both original apps should return to their previous Windows outputs", 12);
        Console.WriteLine("PASS: real user apps controlled through WPF, independent masters, fan-out/mixing, and normal closure. Listening confirmation required.");
    }

    private static void Phase(string description, int seconds)
    { Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} / {seconds}s: {description}."); for (int left = seconds; left > 0; --left) { Thread.Sleep(1000); } }

    /*
     * Name: CheckDeviceOutage
     * Input: Intended disconnected endpoint and an independently playing survivor.
     * Process: Observes user-controlled device removal/reconnection through actual WPF cards.
     * Output: Active-only filtering, retained route, and automatic audio recovery evidence.
     */
    private static void CheckDeviceOutage(DesktopApplication app, string lost, string survivor)
    {
        Console.WriteLine("OUTAGE READY: turn the headphone output OFF; leave it off until detection is reported.");
        app.Wait(() => app.Find("SelectOutput_" + lost) is null, "headphones removed from available outputs", 600000);
        Signal(app, survivor, true);
        app.Wait(() => app.Dump().Contains("Waiting for device", StringComparison.Ordinal), "retained disconnected route");
        Console.WriteLine("OUTAGE DETECTED: headphones disappeared, the route is retained, and the other output continues. Reconnect headphones now.");
        app.Wait(() => app.Find("SelectOutput_" + lost) is not null, "headphones reappeared automatically", 600000);
        Signal(app, lost, true); Signal(app, survivor, true);
        Console.WriteLine("PASS: unavailable headphones hidden, route retained without fallback, automatic reconnection and live output meters.");
        Phase("Both original routes recovered", 15);
    }
    private static void Connect(DesktopApplication app, string source, string output)
    {
        app.Invoke("SelectSource_" + source); app.Invoke("SelectOutput_" + output); app.Invoke("ConnectButton");
        app.Wait(() => app.Element("DisconnectButton").Current.IsEnabled, "confirmed selected connection", 20000);
    }
    private static void Signal(DesktopApplication app, string output, bool present)
    {
        int matches = 0;
        app.Wait(() => { double level = app.Range("Meter_" + output); matches = (present ? level > 0 : level == 0) ? matches + 1 : 0; return matches >= 3; }, "output meter " + (present ? "signal" : "silence"));
        Console.WriteLine($"  Output meter {(present ? "signal" : "silence")}: {app.Range("Meter_" + output):G5}.");
    }
    internal static void Require(bool condition, string description) { if (!condition) { throw new InvalidOperationException(description); } }
}
