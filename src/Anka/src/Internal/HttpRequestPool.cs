namespace Anka;

/// <summary>
/// Lightweight object pool for <see cref="HttpRequest"/> instances.
/// Uses a fixed-size array of slots with per-slot CAS locking.  Each slot
/// holds at most one cached instance; under contention a thread simply moves
/// to the next slot rather than blocking, so the pool is completely lock-free.
/// Pool size is deliberately small (32 slots) to stay inside a single cache
/// line group and avoid false-sharing at very high concurrency.
/// </summary>
internal static class HttpRequestPool
{
    private const int PoolSize = 32;

    private static readonly HttpRequest?[] _slots = new HttpRequest?[PoolSize];

    /// <summary>Rents an <see cref="HttpRequest"/> from the pool, or creates a fresh instance if all slots are occupied.</summary>
    public static HttpRequest Rent()
    {
        for (var i = 0; i < PoolSize; i++)
        {
            var req = Interlocked.Exchange(ref _slots[i], null);
            if (req is not null)
            {
                return req;
            }
        }

        return new HttpRequest();
    }

    /// <summary>
    /// Resets <paramref name="req"/> and returns it to the first available pool slot.
    /// If all slots are occupied the instance is discarded.
    /// </summary>
    public static void Return(HttpRequest req)
    {
        req.ResetForReuse();

        for (var i = 0; i < PoolSize; i++)
        {
            if (Interlocked.CompareExchange(ref _slots[i], req, null) is null)
            {
                return;
            }
        }
        // All slots full — discard; will be collected by GC.
    }
}
