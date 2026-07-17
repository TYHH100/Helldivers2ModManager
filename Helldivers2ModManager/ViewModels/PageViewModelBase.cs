using CommunityToolkit.Mvvm.ComponentModel;

namespace Helldivers2ModManager.ViewModels;

internal abstract class PageViewModelBase : ObservableObject, IDisposable
{
    public abstract string Title { get; }

    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            OnDispose();
        }

        _disposed = true;
    }

    protected virtual void OnDispose() { }
}