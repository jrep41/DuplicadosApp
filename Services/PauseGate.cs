namespace DuplicadosApp.Services;

/// <summary>
/// Compuerta de pausa sin espera activa (0 % CPU y despertar instantáneo al reanudar o cancelar).
/// </summary>
public sealed class PauseGate : IDisposable
{
    private readonly ManualResetEventSlim _resumeEvent = new(initialState: true);
    private volatile bool _isPaused;
    private bool _disposed;

    public bool IsPaused => _isPaused;

    public void Pause()
    {
        if (_disposed)
        {
            return;
        }

        _isPaused = true;
        _resumeEvent.Reset();
    }

    public void Resume()
    {
        if (_disposed)
        {
            return;
        }

        _isPaused = false;
        _resumeEvent.Set();
    }

    /// <summary>
    /// Bloquea el hilo actual mientras la operación esté en pausa y despierta
    /// inmediatamente cuando se reanuda o se solicita cancelación.
    /// </summary>
    public void WaitIfPaused(CancellationToken cancellationToken)
    {
        if (!_isPaused || cancellationToken.IsCancellationRequested || _disposed)
        {
            return;
        }

        try
        {
            _resumeEvent.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // La cancelación despierta la espera de forma limpia.
        }
        catch (ObjectDisposedException)
        {
            // La operación ya finalizó.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isPaused = false;
        _resumeEvent.Set();
        _resumeEvent.Dispose();
    }
}

