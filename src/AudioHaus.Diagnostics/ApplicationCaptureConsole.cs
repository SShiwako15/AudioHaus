using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using AudioHaus.Interop;

namespace AudioHaus.Diagnostics;

/*
 * Name: ApplicationCaptureConsole
 * Input: Application discovery or virtual-cable setup requests.
 * Process: Explains original-playback isolation and shows current Windows snapshots.
 * Output: PIDs, playback endpoints, and actionable setup steps.
 */
internal static class ApplicationCaptureConsole
{
    /*
     * Name: PrintApplications
     * Input: None.
     * Process: Discovers active render sessions and their actual output endpoints.
     * Output: PID/name/endpoint rows; inactive apps may be absent.
     */
    internal static void PrintApplications()
    {
        var sessions = new AudioApplicationEnumerator().Enumerate();
        Console.WriteLine($"Active application audio sessions: {sessions.Count}");
        foreach (var session in sessions.OrderBy(item => item.ProcessId).ThenBy(item => item.Endpoint.DisplayName))
        {
            Console.WriteLine($"PID {session.ProcessId}: {session.ProcessName} -> {session.Endpoint.DisplayName}");
            Console.WriteLine($"  Endpoint ID: {session.Endpoint.Id}");
        }
        Console.WriteLine("Browser renderers may use child PIDs. Capture a browser root to include its tree; separate tabs are not isolated.");
    }

    /*
     * Name: PrintProcesses
     * Input: Optional executable name without .exe.
     * Process: Lists current PIDs to distinguish instances and browser roots.
     * Output: PID/name/window title; protected or exiting processes are skipped.
     */
    internal static void PrintProcesses(string? name)
    {
        foreach (Process process in Process.GetProcesses().OrderBy(item => item.Id))
        {
            using (process)
            {
                try
                {
                    if (name is not null && !string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase)) { continue; }
                    Console.WriteLine($"PID {process.Id}: {process.ProcessName} {process.MainWindowTitle}");
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        }
    }

    /*
     * Name: IsCableCandidate
     * Input: A Windows render endpoint's display name.
     * Process: Recognizes standard VB-CABLE input labels for the setup assistant.
     * Output: A naming hint, not driver or silent-routing verification.
     */
    internal static bool IsCableCandidate(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) && name.Contains("Input", StringComparison.OrdinalIgnoreCase);

    /*
     * Name: PrintCableSetup
     * Input: Current render endpoints.
     * Process: Shows possible installed cable playback endpoints and required manual setup.
     * Output: Exit 0 when a candidate is active, 2 when installation/activation is needed.
     */
    internal static int PrintCableSetup()
    {
        var cables = new AudioEndpointEnumerator().Enumerate()
            .Where(item => item.State == AudioHausEndpointState.Active && IsCableCandidate(item.DisplayName)).ToArray();
        Console.WriteLine($"Possible VB-CABLE playback endpoints: {cables.Length}");
        foreach (var cable in cables) { Console.WriteLine($"{cable.DisplayName}\n  ID: {cable.Id}"); }
        Console.WriteLine("1. If absent, install VB-CABLE from https://vb-audio.com/Cable/ as administrator, then restart Windows as required.");
        Console.WriteLine("2. Keep your normal Windows default output on a physical speaker/headphone.");
        Console.WriteLine("3. Play each source app, then choose CABLE Input for it in Settings > System > Sound > Volume mixer.");
        Console.WriteLine("4. Keep app/session volume unmuted. In recording-device properties for CABLE Output, turn OFF Listen to this device; close cable monitors.");
        Console.WriteLine("5. Run applications to verify the source sessions use that cable, then capture <pid> <cable-input-id> in a route session.");
        Console.WriteLine("One cable can park both apps: process-loopback separates their process trees. AudioHaus outputs must be physical destinations.");
        Console.WriteLine("A name match alone does not prove a virtual device or disabled monitoring. Follow docs/ApplicationCapture.md.");
        return cables.Length > 0 ? 0 : 2;
    }
}
