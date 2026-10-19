using AudioHaus.Interop;
using System.Diagnostics;

/*
 * Name: Program
 * Input: Built automatic renderer and exact parking endpoint ID.
 * Process: Tests real Windows policy movement and restoration with owned applications.
 * Output: PASS evidence; original role preferences restored in finally on all paths.
 */
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.WriteLine("SwitchingChecks <automatic-renderer-exe> <cable-id>"); return 1; }
        try { Run(args[0], args[1]); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    /*
     * Name: Run
     * Input: Renderer executable and active cable endpoint.
     * Process: Saves all roles, parks two instances, verifies actual sessions, and restores after exit.
     * Output: Passed movement/shared-identity/exited-handle checks, or descriptive exception.
     */
    private static void Run(string renderer, string parking)
    {
        Require(ApplicationRoutingPolicy.Probe() == 0, "Windows policy interface unavailable.");
        using Process first = Start(renderer, "330"), second = Start(renderer, "550");
        _ = first.SafeHandle; _ = second.SafeHandle;
        uint pid = (uint)first.Id, other = (uint)second.Id;
        string[] original = Enumerable.Range(0, 3).Select(role => ApplicationRoutingPolicy.ReadPreference(pid, (uint)role)).ToArray();
        string[] secondOriginal = Enumerable.Range(0, 3).Select(role => ApplicationRoutingPolicy.ReadPreference(other, (uint)role)).ToArray();
        Console.WriteLine("Original roles: " + string.Join(" | ", original.Select(item => item.Length == 0 ? "follow-default" : item)));
        Require(original.SequenceEqual(secondOriginal), "Two instances have different Windows policy identities; shared-lease assumptions need revision.");
        try
        {
            string target = ApplicationRoutingPolicy.EndpointPreference(parking);
            for (uint role = 0; role < 3; ++role) { ApplicationRoutingPolicy.WritePreference(pid, role, target); }
            Wait(() => Parked(pid, parking), "first automatic-renderer session moved to cable");
            Console.WriteLine("PASS: original active session actually moved to cable.");
            Console.WriteLine("Shared policy visible on second instance: " + (ApplicationRoutingPolicy.ReadPreference(other, 1) == target));
            for (uint role = 0; role < 3; ++role) { ApplicationRoutingPolicy.WritePreference(other, role, target); }
            Wait(() => Parked(other, parking), "second automatic-renderer session moved to cable");
            Require(ApplicationRoutingPolicy.CheckParking(pid, parking) == 0 && ApplicationRoutingPolicy.CheckParking(other, parking) == 0, "Isolation check failed.");
            Console.WriteLine("PASS: two independent PIDs parked with one shared app preference.");
            second.StandardInput.WriteLine("quit"); second.StandardInput.Flush(); Require(second.WaitForExit(5000), "Second renderer did not exit.");
            bool rejectedExited = false;
            try { ApplicationRoutingPolicy.WritePreference(other, 0, original[0]); }
            catch (InvalidOperationException) { rejectedExited = true; }
            Require(rejectedExited, "Expected Windows to require a live policy identity for exited-PID recovery on this build.");
            Console.WriteLine("PASS: exited-PID rejection detected; live shared identity is required for restoration.");
            for (uint role = 0; role < 3; ++role) { ApplicationRoutingPolicy.WritePreference(pid, role, original[role]); }
            Wait(() => new AudioApplicationEnumerator().Enumerate().Any(item => item.ProcessId == pid && item.Endpoint.Id != parking), "first original stream restored away from cable");
            Require(Enumerable.Range(0, 3).All(role => ApplicationRoutingPolicy.ReadPreference(pid, (uint)role) == original[role]), "Original role preferences were not restored.");
            Console.WriteLine("PASS: exact role preferences restored, including follow-default.");
        }
        finally
        {
            try
            {
                for (uint role = 0; role < 3; ++role)
                {
                    try { ApplicationRoutingPolicy.WritePreference(pid, role, original[role]); }
                    catch (Exception error) { Console.Error.WriteLine("Restore role " + role + ": " + error.Message); }
                }
            }
            finally { try { Stop(first); } finally { Stop(second); } }
        }
    }

    private static bool Parked(uint pid, string parking)
    {
        var rows = new AudioApplicationEnumerator().Enumerate().Where(item => item.ProcessId == pid).ToArray();
        return rows.Length > 0 && rows.All(item => item.Endpoint.Id == parking);
    }

    private static Process Start(string renderer, string frequency)
    {
        var start = new ProcessStartInfo(renderer) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(frequency);
        Process process = Process.Start(start) ?? throw new InvalidOperationException("Renderer did not start.");
        Task<string?> ready = process.StandardOutput.ReadLineAsync();
        Require(ready.Wait(8000) && ready.Result?.StartsWith("READY ", StringComparison.Ordinal) == true, "Renderer did not become ready.");
        process.StandardInput.AutoFlush = true;
        return process;
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited) { process.StandardInput.WriteLine("quit"); if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(); } }
        Require(process.ExitCode == 0, "Renderer failed: " + process.StandardError.ReadToEnd());
    }

    private static void Wait(Func<bool> predicate, string description)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5)) { if (predicate()) { return; } Thread.Sleep(100); }
        throw new TimeoutException(description);
    }

    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
