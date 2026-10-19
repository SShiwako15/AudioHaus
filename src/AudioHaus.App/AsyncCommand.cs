using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AudioHaus.App;

/*
 * Name: AsyncCommand
 * Input: Asynchronous operation, availability predicate, and error handler.
 * Process: Prevents duplicate execution and observes every exception.
 * Output: An awaitable operation exposed through WPF commands.
 */
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;
    private readonly Action<Exception> reportError;
    private bool running;
    public event EventHandler? CanExecuteChanged;

    /*
     * Name: AsyncCommand
     * Input: Operation, availability predicate, and error callback.
     * Process: Retains callbacks without starting work.
     * Output: An idle command.
     */
    public AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> reportError)
    {
        this.execute = execute;
        this.canExecute = canExecute;
        this.reportError = reportError;
    }

    /*
     * Name: CanExecute
     * Input: Unused WPF parameter.
     * Process: Checks current availability and single-execution ownership.
     * Output: True when execution can begin.
     */
    public bool CanExecute(object? parameter) => !running && canExecute();

    /*
     * Name: Execute
     * Input: Unused WPF parameter.
     * Process: Awaits the operation and reports failures through the supplied callback.
     * Output: Refreshed command availability after completion.
     */
    public async void Execute(object? parameter)
    {
        await ExecuteAsync();
    }

    /*
     * Name: ExecuteAsync
     * Input: None.
     * Process: Awaits one available execution while observing errors and balancing availability.
     * Output: Completion after the operation and its command state are settled.
     */
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) { return; }
        running = true;
        Refresh();
        try { await execute(); }
        catch (Exception error) { reportError(error); }
        finally { running = false; Refresh(); }
    }

    /*
     * Name: Refresh
     * Input: None.
     * Process: Requests WPF to reevaluate availability.
     * Output: Updated enabled state.
     */
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
