namespace Greenhaus.MockServer;

/// <summary>
/// The time the mocks live in. It can be moved forward, so a demo or a test can see a subscription renew, a
/// grace period end or a command expire without waiting. Real time passes as usual on top of the offset.
/// </summary>
public sealed class MockClock
{
    private readonly object _gate = new();
    private TimeSpan _offset = TimeSpan.Zero;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate) return DateTimeOffset.UtcNow + _offset;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate) _offset += by;
    }

    public void Reset()
    {
        lock (_gate) _offset = TimeSpan.Zero;
    }
}
