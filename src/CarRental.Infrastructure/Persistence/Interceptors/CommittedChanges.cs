using CarRental.Application.Common.Caching;
using Microsoft.Extensions.Logging;

namespace CarRental.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Per-scope record of the entity types changed by the pending save or transaction; flushed to the
/// <see cref="ICommittedChangesListener"/> singletons once the changes are committed, discarded on rollback (ADR-0044).
/// </summary>
internal sealed partial class CommittedChanges(IEnumerable<ICommittedChangesListener> listeners, ILogger<CommittedChanges> logger)
{
    private readonly ICommittedChangesListener[] _listeners = [.. listeners];
    private readonly HashSet<Type> _pending = [];

    public void Record(IEnumerable<Type> changedTypes)
    {
        ArgumentNullException.ThrowIfNull(changedTypes);
        if (_listeners.Length > 0)
        {
            _pending.UnionWith(changedTypes);
        }
    }

    public void Reset() => _pending.Clear();

    /// <summary>Tells every listener about the committed changes, then forgets them.</summary>
    public void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var changed = new HashSet<Type>(_pending);
        _pending.Clear();
        foreach (var listener in _listeners)
        {
            try
            {
                listener.OnCommitted(changed);
            }
#pragma warning disable CA1031 // A cache listener must never fail a commit that already happened.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogListenerFailed(exception, listener.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Committed-changes listener {Listener} failed.")]
    private partial void LogListenerFailed(Exception exception, string listener);
}
