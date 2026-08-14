using System.Globalization;
using FacilityScheduler.Tests.TestSupport;
using FacilityScheduler;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// FacilityConfiguration.Today/.Now read DateTime.UtcNow directly with no injection seam, so they
/// can't be pinned to a fixed instant here. These tests instead pin the exact TimeZoneInfo
/// conversion logic Today/Now both apply (ToUtcQueryString/FromUtcResponseString), including at the
/// specific instant the H3 bug (architecture doc: "from ~5pm PDT onward, UTC has already rolled to
/// tomorrow") was live-found at.
/// </summary>
public class FacilityConfigurationTests
{
    [Theory]
    [InlineData(2026, 1, 15, 9, 0, 0)]   // deep winter, PST (UTC-8)
    [InlineData(2026, 7, 15, 14, 30, 0)] // deep summer, PDT (UTC-7)
    [InlineData(2026, 3, 1, 1, 0, 0)]    // before spring-forward
    [InlineData(2026, 3, 15, 1, 0, 0)]   // after spring-forward
    [InlineData(2026, 10, 25, 1, 0, 0)]  // before fall-back
    [InlineData(2026, 11, 15, 1, 0, 0)]  // after fall-back
    public void FromUtcResponseString_RoundTripsFacilityLocalTimeThroughBareUtcDigits(int y, int m, int d, int h, int min, int s)
    {
        var facility = TestFacility.Create();
        var local = new DateTime(y, m, d, h, min, s);

        // Bare UTC-digit string, no "Z" - the exact shape Graph actually returns calendarView
        // times in (no outlook.timezone Prefer header) and the shape FromUtcResponseString is
        // built to consume.
        var utcInstant = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), facility.ZoneInfo);
        var responseString = utcInstant.ToString("s");

        Assert.Equal(local, facility.FromUtcResponseString(responseString));
    }

    [Fact]
    public void ToUtcQueryString_ProducesTheCorrectUtcInstant()
    {
        var facility = TestFacility.Create();
        var localNoon = new DateTime(2026, 3, 1, 12, 0, 0); // PST, UTC-8

        var offset = DateTimeOffset.Parse(facility.ToUtcQueryString(localNoon), CultureInfo.InvariantCulture);

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 20, 0, 0, TimeSpan.Zero), offset);
    }

    [Fact]
    public void ToUtcQueryString_ReflectsDaylightSavingOffsetChange()
    {
        var facility = TestFacility.Create();
        var beforeSpringForward = new DateTime(2026, 3, 1, 12, 0, 0);  // PST, UTC-8
        var afterSpringForward = new DateTime(2026, 3, 15, 12, 0, 0);  // PDT, UTC-7

        var beforeUtc = DateTimeOffset.Parse(facility.ToUtcQueryString(beforeSpringForward), CultureInfo.InvariantCulture).UtcDateTime;
        var afterUtc = DateTimeOffset.Parse(facility.ToUtcQueryString(afterSpringForward), CultureInfo.InvariantCulture).UtcDateTime;

        Assert.Equal(20, beforeUtc.Hour); // noon PST -> 8pm UTC
        Assert.Equal(19, afterUtc.Hour);  // noon PDT -> 7pm UTC
    }

    [Fact]
    public void FromUtcResponseString_EveningPacificInstant_StaysOnTheCorrectLocalCalendarDay()
    {
        var facility = TestFacility.Create();

        // 2026-08-04T01:30:00 UTC is 6:30pm on 2026-08-03 in Pacific Daylight Time (UTC-7) - after
        // UTC has already rolled to August 4th, the facility-local calendar day is still August 3rd.
        // Today/Now apply this exact conversion to DateTime.UtcNow; using DateTime.UtcNow.Date
        // directly instead (the original H3 bug) would have read this instant as August 4th.
        var local = facility.FromUtcResponseString("2026-08-04T01:30:00");

        Assert.Equal(new DateTime(2026, 8, 3), local.Date);
        Assert.Equal(18, local.Hour);
        Assert.Equal(30, local.Minute);
    }

    [Fact]
    public void PracticeIceMailConfigured_FalseWhenEitherAddressIsBlank()
    {
        Assert.False(TestFacility.Create(practiceIce: new PracticeIceOptions()).PracticeIceMailConfigured);
        Assert.False(TestFacility.Create(practiceIce: new PracticeIceOptions { ApproverDistributionEmail = "approvers@test.onmicrosoft.com" }).PracticeIceMailConfigured);
        Assert.False(TestFacility.Create(practiceIce: new PracticeIceOptions { MailerMailbox = "mailer@test.onmicrosoft.com" }).PracticeIceMailConfigured);
    }

    [Fact]
    public void PracticeIceMailConfigured_TrueWhenBothAddressesAreSet()
    {
        var facility = TestFacility.Create(practiceIce: new PracticeIceOptions
        {
            ApproverDistributionEmail = "approvers@test.onmicrosoft.com",
            MailerMailbox = "mailer@test.onmicrosoft.com"
        });

        Assert.True(facility.PracticeIceMailConfigured);
    }

    // Deliberately allowed to boot with a blank mail config (unlike TenantDomain/SheetMailboxLocalParts/
    // TimeZone below) - an incremental feature shouldn't stop an already-running deployment from
    // starting just because its own notification path hasn't been configured yet.
    [Fact]
    public void Constructor_BlankPracticeIceMailAddresses_DoesNotThrow()
    {
        var ex = Record.Exception(() => TestFacility.Create(practiceIce: new PracticeIceOptions()));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(-1, 22)]
    [InlineData(6, 25)]
    [InlineData(22, 6)] // start >= end
    public void Constructor_InvalidEligibleHours_Throws(int start, int end)
    {
        Assert.Throws<InvalidOperationException>(() => TestFacility.Create(practiceIce: new PracticeIceOptions
        {
            EligibleStartHour = start,
            EligibleEndHour = end
        }));
    }

    [Fact]
    public void Constructor_NegativeMinLeadHours_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TestFacility.Create(practiceIce: new PracticeIceOptions { MinLeadHours = -1 }));
    }

    [Fact]
    public void Constructor_ZeroMaxHorizonDays_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TestFacility.Create(practiceIce: new PracticeIceOptions { MaxHorizonDays = 0 }));
    }

    // Unlike the PracticeIce mail addresses above, StaffGroupId IS load-bearing (same tier as
    // TenantDomain/SheetMailboxLocalParts/TimeZone) - leaving it blank wouldn't just disable a
    // feature, it would lock everyone, including real staff, out of every staff page under the
    // app's Staff-only fallback authorization policy.
    [Fact]
    public void Constructor_BlankStaffGroupId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TestFacility.Create(staffAccess: new StaffAccessOptions()));
    }

    [Fact]
    public void StaffGroupId_ExposesTheConfiguredValue()
    {
        var facility = TestFacility.Create(staffAccess: new StaffAccessOptions { StaffGroupId = "a-real-group-id" });

        Assert.Equal("a-real-group-id", facility.StaffGroupId);
    }
}
