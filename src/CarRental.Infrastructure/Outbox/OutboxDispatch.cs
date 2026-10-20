namespace CarRental.Infrastructure.Outbox;

/// <summary>Asks the background job server to process the outbox now (in addition to the every-minute schedule).</summary>
public interface IOutboxDispatcher
{
    /// <summary>Best effort: a failure is logged, the recurring run catches up.</summary>
    void TriggerProcessing();
}

/// <summary>Per-scope flag: outbox messages were written by the pending save or transaction.</summary>
internal sealed class OutboxDispatchSignal
{
    public bool IsPending { get; private set; }

    public void MarkPending() => IsPending = true;

    public void Reset() => IsPending = false;

    /// <summary>Returns <see langword="true"/> once per pending signal.</summary>
    public bool TryConsume()
    {
        if (!IsPending)
        {
            return false;
        }

        IsPending = false;
        return true;
    }
}
