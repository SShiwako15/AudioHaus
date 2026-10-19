using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AudioHaus.App;
using AudioHaus.Interop;

namespace AudioHaus.ViewModelChecks;

/*
 * Name: Program
 * Input: None; uses isolated temporary settings without routing applications.
 * Process: Checks actual native-backed discovery, reconciliation, controls, and shutdown.
 * Output: PASS evidence without opening a GUI or requesting application output changes.
 */
internal static class Program
{
    private static int failures;
    [STAThread]
    private static int Main()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckAsync(); }
            catch (Exception error) { failures++; Console.Error.WriteLine(error); }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run(); return failures == 0 ? 0 : 1;
    }

    /*
     * Name: CheckAsync
     * Input: Real routing coordinator and isolated settings directory.
     * Process: Verifies active-only discovery, stable card objects, selection, and lifetime.
     * Output: Assertions covering the redesigned view model instead of removed file controls.
     */
    private static async Task CheckAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AudioHaus.ViewModelChecks", Guid.NewGuid().ToString("N"));
        var coordinator = new RoutingCoordinator(directory);
        var model = new MainViewModel(coordinator);
        int wrongThread = 0;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        model.PropertyChanged += (_, _) => { if (!dispatcher.CheckAccess()) { Interlocked.Increment(ref wrongThread); } };
        try
        {
            await model.InitializeAsync();
            Require(model.IsReady && model.ErrorMessage.Length == 0, model.ErrorMessage);
            var endpoints = new AudioEndpointEnumerator().Enumerate();
            Require(model.Outputs.All(card => endpoints.Any(item => item.Id == card.EndpointId && item.State == AudioHausEndpointState.Active)), "Inactive endpoint is visible.");
            Require(model.Outputs.All(card => card.EndpointId != coordinator.ParkingEndpointId), "Reserved cable appeared as an output.");
            Require(model.Sources.All(card => card.Identity.CreationTime != 0 && card.Identity.ProcessId != (uint)Environment.ProcessId), "Invalid source lifetime/feedback source.");
            Require(!model.ConnectCommand.CanExecute(null) && !model.DisconnectCommand.CanExecute(null), "Unselected actions enabled.");
            if (model.Outputs.Count != 0)
            {
                OutputCardViewModel card = model.Outputs[0]; model.SelectCard(card);
                await Task.Delay(2300);
                Require(ReferenceEquals(card, model.Outputs.FirstOrDefault(item => item.EndpointId == card.EndpointId)), "Periodic refresh replaced a surviving card.");
                Require(ReferenceEquals(card, model.SelectedOutput) && card.IsSelected, "Refresh lost selection.");
                Require(card.VolumePercent == 100 && !card.Muted && !card.IsManaged, "Unconnected master defaults are incorrect.");
            }
            Require(model.Connections.Count == 0 && wrongThread == 0, "Unexpected routes or cross-thread property notification.");
            Console.WriteLine("PASS: active-only batched discovery, reserved-cable exclusion, process lifetimes, stable cards/selection, master defaults, and dispatcher ownership.");
        }
        finally { await model.ShutdownAsync(); await model.ShutdownAsync(); }
        Require(!model.ConnectCommand.CanExecute(null) && !model.DisconnectCommand.CanExecute(null), "Commands enabled after shutdown.");
        for (int cycle = 0; cycle < 3; ++cycle)
        {
            var next = new MainViewModel(new RoutingCoordinator(Path.Combine(directory, cycle.ToString())));
            Task initialize = next.InitializeAsync(); Task close = next.ShutdownAsync(); await Task.WhenAll(initialize, close);
        }
        Console.WriteLine("PASS: idempotent shutdown, disabled actions after closure, and three close-during-initialization cycles.");
    }
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
