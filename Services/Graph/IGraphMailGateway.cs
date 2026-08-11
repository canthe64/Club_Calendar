namespace FacilityScheduler.Services.Graph;

/// <summary>
/// Thin wrapper over Graph's users/{mailbox}/sendMail, mirroring IGraphEventGateway's reason for
/// existing - lets tests substitute a fake instead of driving GraphServiceClient directly.
/// Deliberately a separate interface from IGraphEventGateway: sending mail is a different Graph
/// permission (Mail.Send) against a different mailbox (the mailer, not a sheet or Club Events),
/// unrelated to calendar event CRUD.
/// </summary>
public interface IGraphMailGateway
{
    /// <summary>Sends one plain-text email as <paramref name="fromMailbox"/>. <paramref name="replyToAddress"/>
    /// is set explicitly rather than left to default to the sender, so a reply reaches the person the
    /// message is actually about (the volunteer on an approver-facing notice, the approvers on a
    /// volunteer-facing one) instead of a mailbox nobody treats as an inbox.</summary>
    Task SendMailAsync(string fromMailbox, string toAddress, string? replyToAddress, string subject, string body, CancellationToken ct = default);
}
