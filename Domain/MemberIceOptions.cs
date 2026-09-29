namespace FacilityScheduler.Domain;

/// <summary>A sheet that's free from an option's start time until <see cref="FreeUntil"/> (already
/// clipped to the eligible hours, horizon, and booking season).</summary>
public sealed record SheetFreeRun(string SheetMailbox, DateTime FreeUntil);

/// <summary>
/// A time a member can start hosting practice ice, and every sheet that's free from it (in
/// configured sheet order). Which sheets a session actually covers depends on its length: a session
/// runs on every sheet free for its whole duration, so a longer session can cover fewer sheets.
/// </summary>
public sealed record PracticeIceStartOption(DateTime Start, IReadOnlyList<SheetFreeRun> Sheets)
{
    /// <summary>The sheets a session of this length would run on.</summary>
    public IReadOnlyList<string> SheetsFor(int durationMinutes) =>
        Sheets.Where(s => s.FreeUntil >= Start.AddMinutes(durationMinutes)).Select(s => s.SheetMailbox).ToList();

    /// <summary>The longest session that still keeps at least <paramref name="minSheets"/> sheets
    /// free for its whole length; 0 if fewer than that are free at all.</summary>
    public int MaxDurationMinutes(int minSheets)
    {
        var ends = Sheets.Select(s => s.FreeUntil).OrderByDescending(e => e).ToList();
        return ends.Count < minSheets ? 0 : (int)(ends[minSheets - 1] - Start).TotalMinutes;
    }
}

/// <summary>A bookable make-up game slot: the sheet it would go on (the highest-numbered free one)
/// and its fixed two-hour window.</summary>
public sealed record MakeUpGameOption(DateTime Start, DateTime End, string SheetMailbox);
