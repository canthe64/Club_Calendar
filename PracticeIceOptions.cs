namespace FacilityScheduler;

public class PracticeIceOptions
{
    public const string SectionName = "PracticeIce";

    public int EligibleStartHour { get; set; } = 6;
    public int EligibleEndHour { get; set; } = 22;
    public int MinLeadHours { get; set; } = 48;
    public int MaxHorizonDays { get; set; } = 30;

    /// <summary>Cap on how many pending (not yet approved/declined) requests one member can hold at
    /// once (code review S6) - each successful submission writes a hold across every sheet with no
    /// auto-expiration (§2.2, deliberate), so with no cap a single account could otherwise blanket
    /// the entire booking horizon. The population is B2B-invited members, so likelihood is low, but
    /// the cost of getting this wrong (an early "you already have N pending requests" message vs. a
    /// legitimate host blocked) is asymmetric enough to keep the default generous.</summary>
    public int MaxPendingRequestsPerMember { get; set; } = 3;

    /// <summary>Mail-enabled distribution group notified when a member submits a practice ice
    /// request. Empty until configured at deployment - submission is blocked with an explicit
    /// message rather than silently proceeding with nobody notified.</summary>
    public string ApproverDistributionEmail { get; set; } = string.Empty;

    /// <summary>UPN/address of the mailbox that sends practice ice notifications via Graph
    /// Mail.Send, scoped to just this mailbox by an Application Access Policy (deployment guide).
    /// Empty until configured.</summary>
    public string MailerMailbox { get; set; } = string.Empty;
}
