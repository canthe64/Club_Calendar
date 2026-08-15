using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace FacilityScheduler.Services;

/// <summary>
/// Tracks the public-facing cache keys <see cref="PublicAvailabilityService"/> writes, so any write
/// path can clear them without depending on that service directly.
///
/// It exists to break a dependency cycle: <c>PublicAvailabilityService</c> depends on
/// <c>SheetBookingService</c>, so <c>SheetBookingService</c> cannot depend back on it to invalidate.
/// Both depend on this instead.
///
/// Why it's needed at all: the public cache was originally TTL-only, justified as "it doesn't sit
/// behind a write path." Practice ice hosting made that false - submit/approve/decline mutate
/// exactly the availability those entries cache, so a claimed slot kept showing as free (and
/// declined ice kept showing as taken) for up to the TTL. The live per-sheet conflict check is
/// still the actual guard against a double-booking; this just stops the public view from
/// contradicting it.
/// </summary>
public class ViewCacheRegistry(IMemoryCache cache)
{
    private readonly ConcurrentDictionary<string, byte> _keys = new();

    public void Track(string cacheKey) => _keys[cacheKey] = 0;

    public void InvalidateAll()
    {
        foreach (var key in _keys.Keys)
        {
            cache.Remove(key);
        }
        _keys.Clear();
    }
}
