using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;
using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The calendar subscription feed, /public/calendar.ics (operator request 2026-10-05). The
/// requirement that matters most: it shows exactly what the public calendar shows - same events,
/// titles, privacy rules, filters - except that holds are titled "Hold: ...".
/// </summary>
public class PublicCalendarFeedTests
{
    private static readonly FacilityConfiguration Facility = TestFacility.Create();
    private static readonly DateTime Generated = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static PublicMonthBooking Booking(string title, DateTime start, DateTime end, bool confirmed = true, string category = "League", string? notes = null) =>
        new(title, category, start, end, confirmed, notes);

    private static PublicClubEventLabel OffIce(string title, DateTime start, DateTime end, bool allDay, bool closes = false) =>
        new(title, ClubEventCategory.Closure, start, end, allDay, closes);

    private static string Feed(params PublicMonthBooking[] bookings) =>
        PublicCalendarFeedEndpoint.BuildFeed(new PublicMonthView([.. bookings], []), Facility, Generated);

    /// <summary>Content lines with folding undone (RFC 5545 §3.1).</summary>
    private static List<string> Unfolded(string ics) => [.. ics.Replace("\r\n ", "").Split("\r\n", StringSplitOptions.RemoveEmptyEntries)];

    private static List<List<string>> Events(string ics)
    {
        var events = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in Unfolded(ics))
        {
            if (line == "BEGIN:VEVENT") { current = []; continue; }
            if (line == "END:VEVENT") { events.Add(current!); current = null; continue; }
            current?.Add(line);
        }
        return events;
    }

    private static string Prop(List<string> ev, string name) => ev.Single(l => l.StartsWith(name + ":", StringComparison.Ordinal) || l.StartsWith(name + ";", StringComparison.Ordinal));

    [Fact]
    public void Feed_IsAWellFormedCalendar_WithCrlfLinesAndARefreshHint()
    {
        var ics = Feed(Booking("Tuesday League", new DateTime(2026, 10, 6, 19, 0, 0), new DateTime(2026, 10, 6, 21, 0, 0)));

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.DoesNotContain(ics.Replace("\r\n", ""), c => c == '\n' || c == '\r'); // every break is CRLF
        Assert.Contains("REFRESH-INTERVAL;VALUE=DURATION:PT1H", ics);
        // Named both ways: the de facto X-WR-CALNAME and the standard RFC 7986 NAME.
        Assert.Contains("\r\nX-WR-CALNAME:Test Facility\r\n", ics);
        Assert.Contains("\r\nNAME:Test Facility\r\n", ics);
        Assert.Single(Events(ics));
    }

    [Fact]
    public void ConfirmedBooking_KeepsItsPublicTitle_InUtc()
    {
        // 7PM Pacific Daylight Time is 02:00 UTC the next day.
        var ics = Feed(Booking("Tuesday League · 5 sheets", new DateTime(2026, 10, 6, 19, 0, 0), new DateTime(2026, 10, 6, 21, 0, 0)));
        var ev = Events(ics).Single();

        Assert.Equal("SUMMARY:Tuesday League · 5 sheets", Prop(ev, "SUMMARY"));
        Assert.Equal("DTSTART:20261007T020000Z", Prop(ev, "DTSTART"));
        Assert.Equal("DTEND:20261007T040000Z", Prop(ev, "DTEND"));
        Assert.Equal("STATUS:CONFIRMED", Prop(ev, "STATUS"));
    }

    [Fact]
    public void WinterTime_ConvertsWithTheStandardOffset()
    {
        // 7PM Pacific Standard Time is 03:00 UTC the next day - the DST change is applied, not assumed.
        var ev = Events(Feed(Booking("League", new DateTime(2026, 12, 1, 19, 0, 0), new DateTime(2026, 12, 1, 21, 0, 0)))).Single();

        Assert.Equal("DTSTART:20261202T030000Z", Prop(ev, "DTSTART"));
    }

    [Fact]
    public void Hold_IsTitledHold_AndMarkedTentative()
    {
        var ev = Events(Feed(Booking("Available for Group Events", new DateTime(2026, 10, 7, 12, 0, 0), new DateTime(2026, 10, 7, 17, 0, 0),
            confirmed: false, category: "GroupEvent"))).Single();

        Assert.Equal("SUMMARY:Hold: Available for Group Events", Prop(ev, "SUMMARY"));
        Assert.Equal("STATUS:TENTATIVE", Prop(ev, "STATUS"));
        Assert.Contains("not yet confirmed", Prop(ev, "DESCRIPTION"));
    }

    [Fact]
    public void AllDayOffIceEvent_StaysAllDay_WithAnExclusiveEndDate()
    {
        var day = new DateTime(2026, 10, 9);
        var ics = PublicCalendarFeedEndpoint.BuildFeed(new PublicMonthView([], [OffIce("Facility Closure", day, day, allDay: true, closes: true)]), Facility, Generated);
        var ev = Events(ics).Single();

        Assert.Equal("DTSTART;VALUE=DATE:20261009", Prop(ev, "DTSTART"));
        Assert.Equal("DTEND;VALUE=DATE:20261010", Prop(ev, "DTEND"));
        Assert.Contains("all sheets closed", Prop(ev, "DESCRIPTION"));
    }

    [Fact]
    public void Text_IsEscaped_AndLongLinesAreFoldedWithinUtf8Limits()
    {
        var title = "Smith, Jones; and Co \\ " + new string('é', 60);
        var ics = Feed(Booking(title, new DateTime(2026, 10, 6, 19, 0, 0), new DateTime(2026, 10, 6, 21, 0, 0), notes: "Line one\nLine two"));

        Assert.All(ics.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));
        var ev = Events(ics).Single();
        Assert.Equal("SUMMARY:Smith\\, Jones\\; and Co \\\\ " + new string('é', 60), Prop(ev, "SUMMARY"));
        Assert.Contains("Line one\\nLine two", Prop(ev, "DESCRIPTION"));
    }

    [Fact]
    public void Uid_IsStableAcrossRefreshes_AndDistinctPerEvent()
    {
        var a = Booking("Tuesday League", new DateTime(2026, 10, 6, 19, 0, 0), new DateTime(2026, 10, 6, 21, 0, 0));
        var b = Booking("Tuesday League", new DateTime(2026, 10, 13, 19, 0, 0), new DateTime(2026, 10, 13, 21, 0, 0));

        var first = Events(Feed(a, b)).Select(e => Prop(e, "UID")).ToList();
        var later = Events(PublicCalendarFeedEndpoint.BuildFeed(new PublicMonthView([a, b], []), Facility, Generated.AddHours(5)))
            .Select(e => Prop(e, "UID")).ToList();

        Assert.Equal(first, later);
        Assert.Equal(2, first.Distinct().Count());
        Assert.DoesNotContain(first, uid => uid.Contains('@') && uid.Contains("onmicrosoft")); // no mailbox leaks
    }

    [Fact]
    public void FeedUrl_CarriesTheAppliedFilter_SoTheFeedMatchesThePage()
    {
        var filter = PublicCalendarEndpoint.FilterState.Default with
        {
            IsFiltered = true,
            Categories = [BookingCategory.League]
        };

        Assert.Equal("https://calendar.test.example/public/calendar.ics",
            PublicCalendarFeedEndpoint.FeedUrl("https://calendar.test.example", PublicCalendarEndpoint.FilterState.Default));
        var filtered = PublicCalendarFeedEndpoint.FeedUrl("https://calendar.test.example", filter);
        Assert.StartsWith("https://calendar.test.example/public/calendar.ics?filtered=1", filtered);
        Assert.Contains("categories=League", filtered);
    }

    [Fact]
    public void SubscribeSection_OffersGoogle_Webcal_AndThePlainAddress_WithTheStalenessCaveat()
    {
        var html = PublicCalendarEndpoint.SubscribeSection(PublicCalendarEndpoint.FilterState.Default, "https://calendar.test.example");

        Assert.Contains("https://calendar.google.com/calendar/r?cid=webcal%3A%2F%2Fcalendar.test.example%2Fpublic%2Fcalendar.ics", html);
        Assert.Contains("""href="webcal://calendar.test.example/public/calendar.ics" """, html);
        Assert.Contains("""value="https://calendar.test.example/public/calendar.ics" """, html);
        Assert.Contains("subscriptions sometimes stop updating without warning", html);
    }

    // ---- The feed and the page show the same events ---------------------------------------------

    private static (PublicAvailabilityService Service, SheetBookingService Bookings, ClubEventService ClubEvents, FacilityConfiguration Facility, SchedulingWindowService Window) BuildServices()
    {
        var h = ServiceHarness.Create();
        return (h.Availability, h.Bookings, h.ClubEvents, h.Facility, h.Window);
    }

    [Fact]
    public async Task FeedWindow_IsOneMonthBackToThreeMonthsAhead()
    {
        var (service, bookings, _, facility, _) = BuildServices();
        var today = facility.Today;
        foreach (var (offsetDays, name) in new[] { (-40, "Too old"), (-20, "Recent"), (80, "Upcoming"), (100, "Too far") })
        {
            await bookings.BookAsync(new SheetBooking
            {
                SheetMailbox = TestFacility.SheetMailboxes[0], Start = today.AddDays(offsetDays).AddHours(19), End = today.AddDays(offsetDays).AddHours(21),
                Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = name
            }, "tester");
        }

        var titles = (await service.GetFeedViewAsync()).Bookings.Select(b => b.Title).ToList();

        Assert.Equal(["Recent", "Upcoming"], titles.Order());
    }

    [Fact]
    public async Task FeedAndPage_ShowTheSameEvents_ForTheSameFilter()
    {
        var (service, bookings, clubEvents, facility, _) = BuildServices();
        var day = facility.Today.AddDays(3);
        await bookings.CreateAcrossSheetsAsync(TestFacility.SheetMailboxes, new SheetBooking
        {
            SheetMailbox = "", Start = day.AddHours(19), End = day.AddHours(21), Category = BookingCategory.League,
            State = BookingState.Confirmed, RenterName = "Monday League"
        }, "tester");
        await bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = day.AddHours(10), End = day.AddHours(12),
            Category = BookingCategory.GroupEvent, State = BookingState.Hold
        }, "tester");
        await clubEvents.CreateAsync(new ClubEvent { Title = "Board meeting", Category = ClubEventCategory.Meetings, Start = day.AddHours(18), End = day.AddHours(19) }, "tester");
        var leaguesOnly = PublicCalendarEndpoint.FilterState.Default with { IsFiltered = true, Categories = [BookingCategory.League] };

        foreach (var filter in new[] { PublicCalendarEndpoint.FilterState.Default, leaguesOnly })
        {
            var page = PublicCalendarEndpoint.ApplyFilter(await service.GetDayViewAsync(day), filter);
            var feed = Events(PublicCalendarFeedEndpoint.BuildFeed(
                PublicCalendarEndpoint.ApplyFilter(await service.GetFeedViewAsync(), filter), facility, Generated));

            var pageTitles = page.Bookings.Select(b => b.IsConfirmed ? b.Title : $"Hold: {b.Title}")
                .Concat(page.ClubEvents.Select(ce => ce.Title)).Order().ToList();
            var feedTitles = feed.Select(e => Prop(e, "SUMMARY")["SUMMARY:".Length..]).Order().ToList();
            Assert.Equal(pageTitles, feedTitles);
        }
    }

    [Fact]
    public async Task FeedRespectsThePublishCutoff_LikeThePage()
    {
        var (service, bookings, _, facility, window) = BuildServices();
        await window.SetPublicCutoffAsync(facility.Today.AddDays(10), "tester");
        await bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = facility.Today.AddDays(20).AddHours(19), End = facility.Today.AddDays(20).AddHours(21),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Not yet published"
        }, "tester");

        Assert.DoesNotContain((await service.GetFeedViewAsync()).Bookings, b => b.Title == "Not yet published");
    }

    [Fact]
    public async Task FeedCache_IsClearedByABookingChange()
    {
        var (service, bookings, _, facility, _) = BuildServices();
        Assert.Empty((await service.GetFeedViewAsync()).Bookings);

        await bookings.BookAsync(new SheetBooking
        {
            SheetMailbox = TestFacility.SheetMailboxes[0], Start = facility.Today.AddDays(2).AddHours(19), End = facility.Today.AddDays(2).AddHours(21),
            Category = BookingCategory.League, State = BookingState.Confirmed, RenterName = "Just added"
        }, "tester");

        Assert.Contains((await service.GetFeedViewAsync()).Bookings, b => b.Title == "Just added");
    }
}
