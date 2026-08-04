using System.Runtime.CompilerServices;

// Exposes the small, pure request-parsing/validation helpers on the anonymous-surface endpoints
// (date/range clamping, webhook secret comparison) for direct unit testing, without making them
// public API surface.
[assembly: InternalsVisibleTo("FacilityScheduler.Tests")]
