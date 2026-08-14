namespace FacilityScheduler;

public class StaffAccessOptions
{
    public const string SectionName = "StaffAccess";

    /// <summary>Entra object id of the security group whose members are treated as staff (get the
    /// Staff role claim at sign-in - Services/StaffAccessService.cs). Load-bearing - the app fails
    /// fast at startup if this is blank, same tier as Facility:TenantDomain, since leaving it unset
    /// would lock everyone (including real staff) out of every staff page.</summary>
    public string StaffGroupId { get; set; } = string.Empty;
}
