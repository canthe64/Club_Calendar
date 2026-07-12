namespace FacilityScheduler.Domain;

public static class Sheets
{
    public const string TenantDomain = "anthefamily.onmicrosoft.com";

    public static readonly string[] All =
    [
        $"sheet1@{TenantDomain}",
        $"sheet2@{TenantDomain}",
        $"sheet3@{TenantDomain}",
        $"sheet4@{TenantDomain}",
        $"sheet5@{TenantDomain}",
    ];

    public const string ClubEvents = $"clubevents@{TenantDomain}";
}
