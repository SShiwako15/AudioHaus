using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AudioHaus.App;

/*
 * Name: App
 * Input: Desktop startup and dispatcher lifetime events.
 * Process: Owns the main window and routes unexpected UI faults through orderly closure.
 * Output: A WPF session whose window closes audio ownership before shutdown.
 */
public partial class App : Application
{
    private bool handling_failure;
    private Mutex? instance_mutex;
    private bool owns_instance;
    /*
     * Name: OnStartup
     * Input: Windows application startup arguments.
     * Process: Registers the final UI error boundary and opens the routing workspace.
     * Output: A visible main window; asynchronous audio setup belongs to its view model.
     */
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance_mutex = new Mutex(true, @"Local\AudioHaus.ApplicationRouter", out owns_instance);
        if (!owns_instance)
        {
            MessageBox.Show("AudioHaus is already running.", "AudioHaus", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0); return;
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    /*
     * Name: OnDispatcherUnhandledException
     * Input: Unexpected exception escaping a dispatcher callback.
     * Process: Explains the failure and requests normal window closure for audio cleanup.
     * Output: A handled fatal UI error; expected operation failures stay in workspace banners.
     */
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (handling_failure) { return; }
        handling_failure = true;
        Console.Error.WriteLine(e.Exception);
        string message = "AudioHaus encountered an unexpected error and needs to close.\n\n" + e.Exception.Message;
        if (MainWindow is not null)
        {
            MainWindow.Content = null;
            MessageBox.Show(MainWindow, message, "AudioHaus", MessageBoxButton.OK, MessageBoxImage.Error);
            MainWindow.Close();
        }
        else
        {
            MessageBox.Show(message, "AudioHaus", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /*
     * Name: OnExit
     * Input: Application shutdown result.
     * Process: Removes the dispatcher error subscription after window-owned cleanup.
     * Output: Balanced WPF application lifetime.
     */
    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        if (owns_instance) { instance_mutex?.ReleaseMutex(); }
        instance_mutex?.Dispose();
        base.OnExit(e);
    }
}
