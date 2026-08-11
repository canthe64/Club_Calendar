namespace FacilityScheduler;

public class PracticeIceOptions
{
    public const string SectionName = "PracticeIce";

    public int EligibleStartHour { get; set; } = 6;
    public int EligibleEndHour { get; set; } = 22;
    public int MinLeadHours { get; set; } = 48;
    public int MaxHorizonDays { get; set; } = 30;

    /// <summary>Mail-enabled distribution group notified when a member submits a practice ice
    /// request. Empty until configured at deployment - submission is blocked with an explicit
    /// message rather than silently proceeding with nobody notified.</summary>
    public string ApproverDistributionEmail { get; set; } = string.Empty;

    /// <summary>UPN/address of the mailbox that sends practice ice notifications via Graph
    /// Mail.Send, scoped to just this mailbox by an Application Access Policy (deployment guide).
    /// Empty until configured.</summary>
    public string MailerMailbox { get; set; } = string.Empty;
}
