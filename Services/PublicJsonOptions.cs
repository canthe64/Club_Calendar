using System.Text.Json;
using System.Text.Json.Serialization;

namespace FacilityScheduler.Services;

/// <summary>
/// JSON settings for the Minimal API surface - currently just /api/public/availability, the app's
/// only JSON response (architecture doc §5.4.1).
///
/// Extracted from Program.cs rather than configured inline for the same reason
/// StaffAuthorizationPolicies was: a lambda in Program.cs is unreachable from tests, and this
/// codebase has already shipped one production bug that no test could reach because of exactly that
/// (D75). What's configured here is the published wire format of a public, anonymously-consumable
/// endpoint - worth being able to assert directly.
/// </summary>
public static class PublicJsonOptions
{
    public static void Configure(JsonSerializerOptions options)
    {
        // Without this, enums serialize as their integer ordinal - so clubEvents[].category went out
        // as `2` while api-reference.md had always documented it as `"Closure"` (D79). Names also
        // mean the enum can be reordered freely; it's renaming a member that's now the breaking
        // change, since the name is simultaneously the JSON wire value and the literal string
        // ClubEventService reads back from the Graph event's Categories collection.
        options.Converters.Add(new JsonStringEnumConverter());
    }
}
