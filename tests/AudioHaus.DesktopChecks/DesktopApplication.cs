using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioHaus.DesktopChecks;

/*
 * Name: DesktopApplication
 * Input: Built desktop executable.
 * Process: Owns a real WPF process and drives its public automation controls.
 * Output: Bounded integration operations and cleanup without touching existing apps.
 */
internal sealed class DesktopApplication : IDisposable
{
    private readonly Process process;
    private readonly Task<string> stdout;
    private readonly Task<string> stderr;
    private AutomationElement? window;
    private IntPtr main_window_handle;

    /*
     * Name: DesktopApplication
     * Input: Existing desktop executable path.
     * Process: Starts an isolated app and waits for its accessible main window.
     * Output: Ready UI; launch or startup errors throw and terminate the owned process.
     */
    public DesktopApplication(string executable)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process = Process.Start(start) ?? throw new InvalidOperationException("Desktop process did not start.");
        stdout = process.StandardOutput.ReadToEndAsync();
        stderr = ReadDiagnosticsAsync(process.StandardError);
        try
        {
            Wait(() =>
            {
                window = Windows().FirstOrDefault(item => item.Current.AutomationId == "AudioHausWindow");
                return window is not null && Find("ConnectButton") is not null && Text("StatusText").Length != 0 && !Text("StatusText").StartsWith("Starting", StringComparison.Ordinal);
            }, "WPF router window and initialized status", 30000);
            main_window_handle = new IntPtr(window!.Current.NativeWindowHandle);
            Console.WriteLine($"Opened WPF application PID {process.Id}; HWND={main_window_handle}; root={window!.Current.Name}.");
        }
        catch { Dispose(); throw; }
    }

    /*
     * Name: Wait
     * Input: Observable predicate, failure description, and maximum milliseconds.
     * Process: Requeries UI until its public state matches while checking process liveness.
     * Output: Returns on success; timeout or a crashed owned app throws.
     */
    public void Wait(Func<bool> predicate, string description, int milliseconds = 15000)
    {
        var elapsed = Stopwatch.StartNew();
        Exception? last = null;
        while (elapsed.ElapsedMilliseconds < milliseconds)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Application exited ({process.ExitCode}) while waiting for {description}. {ReadError()}");
            }
            try { if (predicate()) { return; } }
            catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
            { last = error; }
            Thread.Sleep(100);
        }
        string diagnostic;
        try
        {
            diagnostic = string.Join(Environment.NewLine, Windows().Select(item => $"Owned window: {item.Current.Name} [{item.Current.AutomationId}] HWND={item.Current.NativeWindowHandle}\n{Dump(item)}"));
        }
        catch (Exception error) { diagnostic = "Failure diagnostics: " + error.Message; }
        try { Screenshot(Path.Combine(AppContext.BaseDirectory, "Desktop.Failure.png")); }
        catch (Exception error) { diagnostic += "\nScreenshot failure: " + error.Message; }
        throw new TimeoutException($"Timed out waiting for {description}.{(last is null ? string.Empty : " " + last.Message)}\n{diagnostic}");
    }

    /*
     * Name: Find
     * Input: Exact automation ID and optional search root.
     * Process: Searches the owned main window's accessible descendants.
     * Output: Element or null if absent; stale provider errors propagate to the wait loop.
     */
    public AutomationElement? Find(string id, AutomationElement? root = null) =>
        (root ?? MainWindow())?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    /*
     * Name: Element
     * Input: Exact automation ID.
     * Process: Waits for the control to exist.
     * Output: Accessible element; missing controls fail with the UI tree.
     */
    public AutomationElement Element(string id)
    {
        AutomationElement? value = null;
        Wait(() => (value = Find(id)) is not null, id);
        return value!;
    }

    /*
     * Name: Invoke
     * Input: Exact button automation ID.
     * Process: Waits for enablement and invokes the button's real UI command.
     * Output: Command dispatched; missing patterns or failure to enable throws.
     */
    public void Invoke(string id)
    {
        Wait(() => Find(id)?.Current.IsEnabled == true, $"enabled {id}");
        ((InvokePattern)Element(id).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    /*
     * Name: Text
     * Input: Exact text control ID.
     * Process: Reads its accessible name or value.
     * Output: Current visible text, or empty if the control is absent.
     */
    public string Text(string id)
    {
        AutomationElement? item = Find(id);
        if (item is null) { return string.Empty; }
        if (item.TryGetCurrentPattern(ValuePattern.Pattern, out object value)) { return ((ValuePattern)value).Current.Value; }
        return item.Current.Name;
    }

    /*
     * Name: Ids
     * Input: Prefix identifying cards, such as SourceName_.
     * Process: Reads accessible IDs from the actual window and extracts numeric suffixes.
     * Output: Distinct sorted IDs, independent of friendly-name ambiguity.
     */
    public uint[] Ids(string prefix) => Elements(MainWindow()!).Select(item => item.Current.AutomationId)
        .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
        .Select(id => uint.TryParse(id.AsSpan(prefix.Length), out uint value) ? value : 0)
        .Where(value => value != 0).Distinct().OrderBy(value => value).ToArray();

    /*
     * Name: Keys
     * Input: An automation-ID prefix.
     * Process: Reads opaque endpoint/process-lifetime suffixes from current controls.
     * Output: Distinct keys without interpreting endpoint IDs as numbers.
     */
    public string[] Keys(string prefix) => Elements(MainWindow()!).Select(item => item.Current.AutomationId)
        .Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).Select(id => id[prefix.Length..]).Distinct().ToArray();

    /*
     * Name: AddFile
     * Input: Existing audio file path.
     * Process: Invokes Add Files and fills the standard Windows file picker.
     * Output: New source-card ID; decoder errors or missing cards fail.
     */
    public uint AddFile(string path)
    {
        uint[] before = Ids("SourceName_");
        Invoke("AddFilesButton");
        PickFile(path);
        uint added = 0;
        Wait(() =>
        {
            uint[] additions = Ids("SourceName_").Except(before).ToArray();
            if (additions.Length == 1) { added = additions[0]; return true; }
            if (!string.IsNullOrWhiteSpace(Text("ErrorText")))
            { throw new InvalidDataException($"Adding {path} failed: {Text("ErrorText")}"); }
            return false;
        }, $"source card for {Path.GetFileName(path)}");
        return added;
    }

    /*
     * Name: PickFile
     * Input: Absolute file path after the Open dialog has been requested.
     * Process: Uses the native dialog's filename edit and Open button through UIA.
     * Output: Dialog accepted without keyboard or mouse injection; missing controls fail.
     */
    public void PickFile(string path)
    {
        AutomationElement? dialog = null;
        Wait(() =>
        {
            var dialogCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
            var openCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1"));
            var filenameCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"));
            dialog = Windows().SelectMany(root => new[] { root }.Concat(
                    root.FindAll(TreeScope.Descendants, dialogCondition).Cast<AutomationElement>()))
                .FirstOrDefault(item => item.Current.NativeWindowHandle != window!.Current.NativeWindowHandle &&
                    item.Current.Name == "Add audio files to AudioHaus" &&
                    item.FindFirst(TreeScope.Descendants, filenameCondition) is not null &&
                    item.FindFirst(TreeScope.Descendants, openCondition) is not null);
            return dialog is not null;
        }, "Windows file picker");
        AutomationElement[] edits = Elements(dialog!).Where(item => item.Current.ControlType == ControlType.Edit &&
            item.TryGetCurrentPattern(ValuePattern.Pattern, out _)).ToArray();
        AutomationElement? edit = edits.FirstOrDefault(item => item.Current.AutomationId is "1148" or "1001") ??
            edits.FirstOrDefault(item => item.Current.Name.Contains("File name", StringComparison.OrdinalIgnoreCase));
        if (edit is null) { throw new InvalidOperationException("The Open dialog did not expose its filename edit.\n" + Dump(dialog)); }
        ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(Path.GetFullPath(path));
        Console.WriteLine($"Picker value: {((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value}");
        AutomationElement? open = Elements(dialog!).FirstOrDefault(item => item.Current.ControlType == ControlType.Button &&
            (item.Current.AutomationId == "1" || item.Current.Name is "Open" or "&Open"));
        if (open is null) { throw new InvalidOperationException("The Open dialog did not expose its Open button."); }
        ((InvokePattern)open.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    /*
     * Name: Select
     * Input: Combo box ID and exact automation ID of its desired item.
     * Process: Expands the picker and selects its item using SelectionItemPattern.
     * Output: Selected item; unavailable choices fail without choosing by display name.
     */
    public void Select(string comboId, string itemId)
    {
        AutomationElement combo = Element(comboId);
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expand))
        { ((ExpandCollapsePattern)expand).Expand(); }
        AutomationElement? item = null;
        Wait(() =>
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, itemId),
                new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true));
            item = combo.FindFirst(TreeScope.Descendants, condition) ??
                Windows().Select(candidate => candidate.FindFirst(TreeScope.Descendants, condition)).FirstOrDefault(candidate => candidate is not null);
            if (item is null) { return false; }
            if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object scroll)) { ((ScrollItemPattern)scroll).ScrollIntoView(); }
            if (!item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selection)) { return false; }
            ((SelectionItemPattern)selection).Select();
            return true;
        }, $"{itemId} in {comboId}");
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expand))
        { ((ExpandCollapsePattern)expand).Collapse(); }
    }

    /*
     * Name: Toggle
     * Input: Check-box ID and desired checked state.
     * Process: Activates the actual toggle only when its current state differs.
     * Output: Requested state becomes observable; disabled controls or failed updates throw.
     */
    public void Toggle(string id, bool enabled)
    {
        Wait(() => Find(id)?.Current.IsEnabled == true, $"enabled {id}");
        Element(id).SetFocus();
        TogglePattern pattern = (TogglePattern)Element(id).GetCurrentPattern(TogglePattern.Pattern);
        if ((pattern.Current.ToggleState == ToggleState.On) != enabled) { pattern.Toggle(); }
        int matches = 0;
        Wait(() => { matches = IsChecked(id) == enabled ? matches + 1 : 0; return matches >= 3; }, $"{id} checked={enabled}");
    }

    /*
     * Name: IsChecked
     * Input: Check-box ID.
     * Process: Queries its public TogglePattern state.
     * Output: True only when checked; missing controls/patterns throw.
     */
    public bool IsChecked(string id) => ((TogglePattern)Element(id).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    /*
     * Name: SetRange
     * Input: Slider ID and finite in-range value.
     * Process: Sets RangeValuePattern, triggering the same binding as a user drag.
     * Output: Requested slider value; unsupported range/pattern errors propagate.
     */
    public void SetRange(string id, double value)
    {
        Wait(() => Find(id)?.Current.IsEnabled == true, $"enabled {id}");
        RangeValuePattern range = (RangeValuePattern)Element(id).GetCurrentPattern(RangeValuePattern.Pattern);
        range.SetValue(value);
        Wait(() => Math.Abs(Range(id) - value) < 0.05, $"{id} value {value}");
    }

    /*
     * Name: SetText
     * Input: Edit ID and replacement text.
     * Process: Sets ValuePattern and moves focus to the main refresh control to commit edits.
     * Output: Updated text; validation failures remain visible for assertions.
     */
    public void SetText(string id, string text)
    {
        ((ValuePattern)Element(id).GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);
        Element("RefreshOutputsButton").SetFocus();
    }

    /*
     * Name: Range
     * Input: Slider or meter ID.
     * Process: Reads its public numeric range value.
     * Output: Current value; missing pattern/control errors propagate.
     */
    public double Range(string id) => ((RangeValuePattern)Element(id).GetCurrentPattern(RangeValuePattern.Pattern)).Current.Value;

    /*
     * Name: Screenshot
     * Input: Destination PNG path.
     * Process: Copies the owned window's desktop rectangle into a standalone PNG.
     * Output: Screenshot of the rendered app; capture failures throw.
     */
    public void Screenshot(string path)
    {
        if (!GetWindowRect(main_window_handle, out NativeRect rectangle))
        { throw new InvalidOperationException("The owned window has no rectangle."); }
        Rect bounds = new(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
        int width = checked((int)Math.Ceiling(bounds.Width)), height = checked((int)Math.Ceiling(bounds.Height));
        if (width <= 0 || height <= 0) { throw new InvalidOperationException("Window is not visible for screenshot capture."); }
        IntPtr desktop = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (desktop == IntPtr.Zero || (memory = CreateCompatibleDC(desktop)) == IntPtr.Zero ||
                (bitmap = CreateCompatibleBitmap(desktop, width, height)) == IntPtr.Zero)
            { throw new InvalidOperationException("Windows could not allocate screenshot resources."); }
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
            { throw new InvalidOperationException("Windows could not select the screenshot bitmap."); }
            if (!BitBlt(memory, 0, 0, width, height, desktop, (int)bounds.Left, (int)bounds.Top, 0x00CC0020))
            { throw new InvalidOperationException("Desktop screenshot copy failed."); }
            BitmapSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(source));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using FileStream stream = File.Create(path);
            png.Save(stream);
            Console.WriteLine($"Screenshot: {Path.GetFullPath(path)}");
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) { SelectObject(memory, previous); }
            if (bitmap != IntPtr.Zero) { DeleteObject(bitmap); }
            if (memory != IntPtr.Zero) { DeleteDC(memory); }
            if (desktop != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, desktop); }
        }
    }

    /*
     * Name: Dump
     * Input: Optional owned subtree.
     * Process: Describes a bounded set of accessible controls for failed-test diagnostics.
     * Output: IDs, names, types, and enablement without enumerating unrelated apps.
     */
    public string Dump(AutomationElement? root = null) => string.Join(Environment.NewLine,
        Elements(root ?? MainWindow()!).Where(DiagnosticElement).Take(180).Select(item =>
            $"{item.Current.ControlType.ProgrammaticName} [{item.Current.AutomationId}] {item.Current.Name} enabled={item.Current.IsEnabled}"));

    /*
     * Name: DiagnosticElement
     * Input: Accessible element inside the owned window.
     * Process: Excludes file-list and folder-tree rows and their descendants from logs.
     * Output: True for controls useful to diagnose UI actions without library listings.
     */
    private static bool DiagnosticElement(AutomationElement item)
    {
        AutomationElement? ancestor = item;
        for (int depth = 0; ancestor is not null && depth < 16; ++depth)
        {
            if (ancestor.Current.ControlType == ControlType.ListItem || ancestor.Current.ControlType == ControlType.TreeItem)
            { return false; }
            if (ancestor.Current.ControlType == ControlType.Window) { break; }
            ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
        }
        return true;
    }

    /*
     * Name: Close
     * Input: None.
     * Process: Requests a real window close and waits for asynchronous native cleanup.
     * Output: Clean process exit; shutdown timeout, crash, or stderr fails.
     */
    public void Close()
    {
        if (!process.HasExited) { ((WindowPattern)MainWindow()!.GetCurrentPattern(WindowPattern.Pattern)).Close(); }
        Program.Require(process.WaitForExit(15000), "WPF app did not close within fifteen seconds.");
        string error = ReadError();
        Program.Require(process.ExitCode == 0 && string.IsNullOrWhiteSpace(error), $"WPF exit {process.ExitCode}: {error}");
        Console.WriteLine("PASS: WPF window closed and owned native graph shut down cleanly.");
    }

    /*
     * Name: Dispose
     * Input: None.
     * Process: Requests normal restoration after failure, then terminates an unresponsive owned app.
     * Output: No orphaned desktop process; normal Close remains the verified shutdown path.
     */
    public void Dispose()
    {
        if (!process.HasExited)
        {
            try { process.CloseMainWindow(); process.WaitForExit(15000); } catch (InvalidOperationException) { }
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
        }
        string error = ReadError();
        if (!string.IsNullOrWhiteSpace(error)) { Console.WriteLine("Owned app stderr: " + error); }
        process.Dispose();
    }

    /*
     * Name: Windows
     * Input: None.
     * Process: Filters desktop top-level windows by the owned application's exact PID.
     * Output: Current main, picker, and popup windows belonging to this test process.
     */
    private AutomationElement[] Windows() => AutomationElement.RootElement.FindAll(TreeScope.Children,
        new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)).Cast<AutomationElement>().ToArray();

    /*
     * Name: MainWindow
     * Input: None.
     * Process: Refreshes the UIA root from the owned window handle after modal dialogs.
     * Output: Current main-window provider or the startup candidate before discovery.
     */
    private AutomationElement? MainWindow() => main_window_handle == IntPtr.Zero ? window : AutomationElement.FromHandle(main_window_handle);

    /*
     * Name: ReadDiagnosticsAsync
     * Input: The owned application's error stream.
     * Process: Retains diagnostics and forwards each line while the process is still alive.
     * Output: Complete error text after exit without hiding a stalled application's fault.
     */
    private static async Task<string> ReadDiagnosticsAsync(StreamReader reader)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is string line)
        {
            lines.Add(line);
            Console.WriteLine("App diagnostic: " + line);
        }
        return string.Join(Environment.NewLine, lines);
    }

    /*
     * Name: NativeRect
     * Input: Windows screen coordinates.
     * Process: Matches the Win32 RECT memory layout.
     * Output: The owned window's capture rectangle.
     */
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    /*
     * Name: GetWindowRect
     * Input: Owned window handle and destination rectangle.
     * Process: Queries bounds independently of a potentially failed UIA provider.
     * Output: True with screen coordinates, or false when the window is unavailable.
     */
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    /*
     * Name: Elements
     * Input: Owned root control.
     * Process: Enumerates its accessible descendants.
     * Output: A snapshot used by queries and failure diagnostics.
     */
    private static IEnumerable<AutomationElement> Elements(AutomationElement root) =>
        root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>();

    /*
     * Name: ReadError
     * Input: None.
     * Process: Reads drained output only after the owned process has exited.
     * Output: Diagnostic stderr followed by stdout; empty while still running.
     */
    private string ReadError() => process.HasExited ? stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult() : string.Empty;

    /*
     * Name: GetDC
     * Input: Null for the desktop.
     * Process: Acquires a GDI device context.
     * Output: Context handle or zero on failure.
     */
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    /*
     * Name: ReleaseDC
     * Input: Original window and acquired context.
     * Process: Releases a GetDC context.
     * Output: Windows release result.
     */
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    /*
     * Name: CreateCompatibleDC
     * Input: Desktop device context.
     * Process: Allocates an in-memory GDI context.
     * Output: Context handle or zero on failure.
     */
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    /*
     * Name: CreateCompatibleBitmap
     * Input: Device context and capture dimensions.
     * Process: Allocates a compatible bitmap.
     * Output: Owned bitmap handle or zero on failure.
     */
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    /*
     * Name: SelectObject
     * Input: Memory context and bitmap handle.
     * Process: Selects a bitmap for subsequent capture or restores the previous selection.
     * Output: Previous object handle or a Windows failure sentinel.
     */
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    /*
     * Name: DeleteObject
     * Input: Owned screenshot bitmap.
     * Process: Releases its GDI allocation after deselection.
     * Output: Windows success flag.
     */
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    /*
     * Name: DeleteDC
     * Input: Owned in-memory device context.
     * Process: Releases the GDI context allocation.
     * Output: Windows success flag.
     */
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    /*
     * Name: BitBlt
     * Input: Desktop source rectangle and selected in-memory destination bitmap.
     * Process: Copies the rendered pixels with SRCCOPY.
     * Output: Windows success flag; false makes screenshot verification fail.
     */
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
}
