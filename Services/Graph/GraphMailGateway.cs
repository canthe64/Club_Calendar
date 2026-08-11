using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace FacilityScheduler.Services.Graph;

/// <summary>The only class in this app that talks to GraphServiceClient directly for mail - see
/// IGraphMailGateway for why this boundary exists. Requires the Mail.Send application permission,
/// scoped by an Application Access Policy to just the mailer mailbox (deployment guide) - the same
/// mechanism already used to scope Calendars.ReadWrite to the sheet mailboxes.</summary>
public class GraphMailGateway(GraphServiceClient graphClient) : IGraphMailGateway
{
    public Task SendMailAsync(string fromMailbox, string toAddress, string? replyToAddress, string subject, string body, CancellationToken ct = default)
    {
        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Text, Content = body },
            ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = toAddress } }]
        };

        if (!string.IsNullOrWhiteSpace(replyToAddress))
        {
            message.ReplyTo = [new Recipient { EmailAddress = new EmailAddress { Address = replyToAddress } }];
        }

        return graphClient.Users[fromMailbox].SendMail.PostAsync(new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = false
        }, cancellationToken: ct);
    }
}
