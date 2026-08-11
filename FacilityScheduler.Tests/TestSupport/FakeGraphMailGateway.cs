using FacilityScheduler.Services.Graph;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>In-memory stand-in for GraphMailGateway - records every call so a test can assert who
/// got mailed, from where, and with what reply-to, without touching Graph.</summary>
public class FakeGraphMailGateway : IGraphMailGateway
{
    public List<SentMail> Sent { get; } = [];

    /// <summary>When set, SendMailAsync throws instead of recording - simulates a Graph-side
    /// failure (missing Mail.Send consent, an Application Access Policy that doesn't yet scope the
    /// mailbox) so callers can be tested for the live-found 2026-08-09 bug: a mail failure must
    /// never make an already-successful booking write look like it failed.</summary>
    public bool ThrowOnSend { get; set; }

    public Task SendMailAsync(string fromMailbox, string toAddress, string? replyToAddress, string subject, string body, CancellationToken ct = default)
    {
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("Simulated Graph sendMail failure.");
        }

        Sent.Add(new SentMail(fromMailbox, toAddress, replyToAddress, subject, body));
        return Task.CompletedTask;
    }

    public record SentMail(string From, string To, string? ReplyTo, string Subject, string Body);
}
