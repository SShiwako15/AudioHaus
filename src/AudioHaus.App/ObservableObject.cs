using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioHaus.App;

/*
 * Name: ObservableObject
 * Input: Property changes from the UI dispatcher.
 * Process: Publishes binding notifications when values change.
 * Output: A reusable observable view-model base.
 */
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /*
     * Name: SetProperty
     * Input: Storage, replacement value, and property name.
     * Process: Replaces different values and notifies bindings.
     * Output: True when the value changed.
     */
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) { return false; }
        storage = value;
        OnPropertyChanged(name);
        return true;
    }

    /*
     * Name: OnPropertyChanged
     * Input: Property name.
     * Process: Notifies current subscribers on the calling UI thread.
     * Output: Updated bindings.
     */
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
