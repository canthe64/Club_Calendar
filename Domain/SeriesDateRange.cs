namespace FacilityScheduler.Domain;

/// <summary>
/// The recurrence pattern's own configured first/last date for a series - what staff typed into the
/// New Series wizard's First date/Last date (architecture doc §4.5), not "every date that's still
/// actually live" (that's <c>SheetBookingService</c>'s own internal
/// <c>SeriesOccurrenceWindowsAsync</c>, used only for conflict-checking a sheet add). Read from a
/// single Graph GET of the series master's <c>Recurrence.Range</c> - see
/// <see cref="Services.SheetBookingService.GetSeriesRangeAsync"/>.
///
/// <see cref="LastDate"/> is null for a NoEnd/Numbered recurrence range - this app only ever creates
/// EndDate-range series, so a null <see cref="LastDate"/> here means hand-edited or otherwise
/// foreign data, not a bug.
/// </summary>
public readonly record struct SeriesDateRange(DateTime FirstDate, DateTime? LastDate);
