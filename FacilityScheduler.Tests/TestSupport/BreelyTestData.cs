using System.Globalization;
using FacilityScheduler.Services;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>Builds BreelyEvent payloads in the exact "start_date"/"start_time" split-string shape
/// BreelyBookingProcessor.TryParseWindow parses ("Sep 25, 2026" / "9:00am"), so tests exercise the
/// real parsing path instead of constructing an already-parsed DateTime.</summary>
public static class BreelyTestData
{
    public static (string StartDate, string StartTime) SplitForBreely(DateTime facilityLocalStart) => (
        facilityLocalStart.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
        facilityLocalStart.ToString("h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant());

    public static BreelyEvent MakeEvent(long id, DateTime facilityLocalStart, int durationMinutes,
        string bookedWith = "Curling Sheet", string? clientName = "Test Renter", bool canceled = false, string? adminUrl = "https://breely.example/admin/1")
    {
        var (date, time) = SplitForBreely(facilityLocalStart);
        return new BreelyEvent
        {
            Id = id,
            StartDate = date,
            StartTime = time,
            DurationInMinutes = durationMinutes,
            BookedWith = bookedWith,
            ClientFullName = clientName,
            Canceled = canceled,
            AdminUrl = adminUrl
        };
    }
}
