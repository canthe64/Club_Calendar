using Bunit;
using FacilityScheduler.Components.Calendar;
using FacilityScheduler.Domain;

namespace FacilityScheduler.Tests;

/// <summary>URLs in notes render as links in the staff dialogs (2026-11-10).</summary>
public class NoteLinksTests : BunitContext
{
    private static string Describe(string? text) =>
        string.Join("|", NoteLinks.Split(text).Select(s => s.IsUrl ? $"<{s.Text}>" : s.Text));

    [Theory]
    [InlineData("Bring your own broom.", "Bring your own broom.")]
    [InlineData("Sign up here: https://docs.google.com/spreadsheets/d/12L9/edit?usp=sharing",
        "Sign up here: |<https://docs.google.com/spreadsheets/d/12L9/edit?usp=sharing>")]
    [InlineData("Details at https://example.com/a. Next", "Details at |<https://example.com/a>|. Next")]
    [InlineData("(see https://example.com/b) ok", "(see |<https://example.com/b>|) ok")]
    [InlineData("https://en.wikipedia.org/wiki/Curling_(sport), done", "<https://en.wikipedia.org/wiki/Curling_(sport)>|, done")]
    [InlineData("a http://x.example/1 and https://y.example/2!", "a |<http://x.example/1>| and |<https://y.example/2>|!")]
    [InlineData("http://plain.example/x?a=1&b=2", "<http://plain.example/x?a=1&b=2>")]
    [InlineData("line one\nhttps://example.com\nline three", "line one\n|<https://example.com>|\nline three")]
    public void Split_FindsHttpUrlsAndLeavesTrailingPunctuationAsText(string text, string expected)
    {
        Assert.Equal(expected, Describe(text));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://files.example/x")]
    [InlineData("www.example.com")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void Split_NonHttpTextIsNeverALink(string text)
    {
        Assert.DoesNotContain(NoteLinks.Split(text), s => s.IsUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Split_EmptyNotesHaveNoSegments(string? text)
    {
        Assert.Empty(NoteLinks.Split(text));
    }

    [Fact]
    public void Split_RejoinsToTheOriginalText()
    {
        const string text = "Sign up (https://a.example/x). Or https://b.example/y_(z)!";
        Assert.Equal(text, string.Concat(NoteLinks.Split(text).Select(s => s.Text)));
    }

    [Fact]
    public void BookingDetail_NotesUrlRendersAsNewTabLink_RestStaysEncodedText()
    {
        var booking = new SheetBooking
        {
            SheetMailbox = "sheet1@example.com",
            EventId = "evt-1",
            Category = BookingCategory.Other,
            State = BookingState.Confirmed,
            RenterName = "Competitive Practice Ice",
            Start = new DateTime(2026, 11, 10, 18, 0, 0),
            End = new DateTime(2026, 11, 10, 20, 0, 0),
            Notes = "<b>Sign up</b> here: https://docs.google.com/spreadsheets/d/12L9/edit."
        };

        var cut = Render<BookingDetailModal>(p => p.Add(m => m.Group, new List<SheetBooking> { booking }));

        var link = cut.Find("a[href='https://docs.google.com/spreadsheets/d/12L9/edit']");
        Assert.Equal("https://docs.google.com/spreadsheets/d/12L9/edit", link.TextContent);
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        Assert.Contains("&lt;b&gt;Sign up&lt;/b&gt; here: ", cut.Markup);
        Assert.Empty(cut.FindAll("b"));
    }

    [Fact]
    public void ClubEventDetail_NotesUrlRendersAsLink()
    {
        var ce = new ClubEvent
        {
            Title = "Fall Bonspiel",
            Category = ClubEventCategory.Competitions,
            Start = new DateTime(2026, 11, 14),
            End = new DateTime(2026, 11, 14),
            IsAllDay = true,
            Notes = "Draw: https://example.com/draw"
        };

        var cut = Render<ClubEventDetailModal>(p => p.Add(m => m.ClubEvent, ce));

        Assert.Equal("https://example.com/draw", cut.Find("a[href='https://example.com/draw']").TextContent);
    }
}
