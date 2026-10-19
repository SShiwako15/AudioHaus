using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AudioHaus.TimingChecks;

/*
 * Name: OwnedProcess
 * Input: Test helper executable and argument tokens.
 * Process: Owns redirected commands, output, bounded shutdown, and debugger-style stalls.
 * Output: An isolated child; no operations target existing user processes.
 */
internal sealed class OwnedProcess : IDisposable
{
    private readonly Process process;
    private readonly Task<string> stdout, stderr;
    public int Id => process.Id;

    /*
     * Name: OwnedProcess
     * Input: Executable and argument tokens.
     * Process: Starts a hidden child and drains both output pipes asynchronously.
     * Output: Command-ready process; launch failures propagate.
     */
    public OwnedProcess(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        process = Process.Start(start)!;
        process.StandardInput.AutoFlush = true;
        stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
    }

    /*
     * Name: Send
     * Input: One console command.
     * Process: Writes and flushes the helper's stdin.
     * Output: No value; broken pipes propagate.
     */
    public void Send(string command) => process.StandardInput.WriteLine(command);

    /*
     * Name: Stall
     * Input: Milliseconds to withhold all test-child scheduling.
     * Process: Suspends only owned child threads and resumes every acquired handle in finally.
     * Output: Real WASAPI starvation; suspension errors fail without leaving threads suspended.
     */
    public void Stall(int milliseconds)
    {
        var suspended = new List<IntPtr>();
        try
        {
            process.Refresh();
            foreach (ProcessThread thread in process.Threads)
            {
                IntPtr handle = OpenThread(2, false, (uint)thread.Id);
                if (handle == IntPtr.Zero) { continue; }
                if (SuspendThread(handle) == uint.MaxValue) { CloseHandle(handle); continue; }
                suspended.Add(handle);
            }
            Program.Require(suspended.Count > 0, "Could not stall the test-owned router.");
            Thread.Sleep(milliseconds);
        }
        finally
        {
            foreach (IntPtr handle in suspended)
            {
                ResumeThread(handle);
                CloseHandle(handle);
            }
        }
    }

    /*
     * Name: Quit
     * Input: Expected number of rejected console commands, normally zero.
     * Process: Sends quit and requires clean shutdown within ten seconds.
     * Output: Complete stdout; nonzero exit or unexpected stderr fails with diagnostics.
     */
    public string Quit(int commandErrors = 0)
    {
        if (!process.HasExited) { Send("quit"); process.StandardInput.Close(); }
        Program.Require(process.WaitForExit(10000), "Helper failed to shut down.");
        string output = stdout.GetAwaiter().GetResult(), errors = stderr.GetAwaiter().GetResult();
        string[] lines = errors.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Program.Require(process.ExitCode == 0 && lines.Length == commandErrors &&
            Regex.Matches(errors, "Command error:").Count == commandErrors, $"Helper failed: {errors}\n{output}");
        return output;
    }

    /*
     * Name: Dispose
     * Input: None.
     * Process: Terminates an unfinished owned child after a failed check and releases its handle.
     * Output: No orphaned helper; existing applications remain untouched.
     */
    public void Dispose()
    {
        if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
        process.Dispose();
    }

    /*
     * Name: OpenThread
     * Input: Requested rights, inheritance flag, and owned child thread ID.
     * Process: Opens a Windows thread handle.
     * Output: Handle or zero on failure.
     */
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    /*
     * Name: SuspendThread
     * Input: Owned thread handle.
     * Process: Adds one debugger suspension.
     * Output: Previous count or uint.MaxValue on failure.
     */
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr thread);
    /*
     * Name: ResumeThread
     * Input: Owned suspended thread handle.
     * Process: Removes exactly this check's suspension.
     * Output: Previous count or uint.MaxValue on failure.
     */
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    /*
     * Name: CloseHandle
     * Input: Owned Windows handle.
     * Process: Releases the handle.
     * Output: Windows success flag.
     */
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
