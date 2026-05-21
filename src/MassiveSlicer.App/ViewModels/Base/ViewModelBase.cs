using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MassiveSlicer.ViewModels.Base;

/// <summary>
/// Base class for all ViewModels. Implements <see cref="INotifyPropertyChanged"/>
/// and provides the <see cref="SetField{T}"/> helper to reduce boilerplate.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for the given property name.
    /// </summary>
    /// <param name="propertyName">Name of the property that changed. Inferred by the compiler when omitted.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Sets <paramref name="field"/> to <paramref name="value"/> and raises
    /// <see cref="PropertyChanged"/> only when the value actually changed.
    /// </summary>
    /// <typeparam name="T">The property type.</typeparam>
    /// <param name="field">Backing field reference.</param>
    /// <param name="value">New value to assign.</param>
    /// <param name="propertyName">Property name, inferred by the compiler.</param>
    /// <returns><c>true</c> if the value changed; <c>false</c> otherwise.</returns>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
