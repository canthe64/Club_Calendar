using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

public class BreelyBookingWebhookEndpointTests
{
    [Fact]
    public void SecretsMatch_IdenticalSecrets_ReturnsTrue()
    {
        Assert.True(BreelyBookingWebhookEndpoint.SecretsMatch("correct-horse-battery-staple", "correct-horse-battery-staple"));
    }

    [Theory]
    [InlineData("correct-secret", "wrong-secret")]
    [InlineData("correct-secret", "")]
    [InlineData("correct-secret", "correct-secre")] // shorter prefix
    [InlineData("correct-secret", "correct-secretx")] // longer
    public void SecretsMatch_DifferingSecrets_ReturnsFalse(string expected, string provided)
    {
        Assert.False(BreelyBookingWebhookEndpoint.SecretsMatch(expected, provided));
    }

    [Fact]
    public void RedactAndTruncate_ShortPayload_ReturnedUnchanged()
    {
        var body = """{"event":{"id":482792}}""";

        Assert.Equal(body, BreelyBookingWebhookEndpoint.RedactAndTruncate(body));
    }

    [Fact]
    public void RedactAndTruncate_RedactsThePiiFieldsButNothingElse()
    {
        var body = """{"client_full_name":"Michelle Brant","client_email":"michelleb@cercanolp.com","client_phone":"555-0100","company_or_group_name:":"Cercano Management LLC"}""";

        var result = BreelyBookingWebhookEndpoint.RedactAndTruncate(body);

        Assert.Contains(@"""client_full_name"":""[redacted]""", result);
        Assert.Contains(@"""client_email"":""[redacted]""", result);
        Assert.Contains(@"""client_phone"":""[redacted]""", result);
        Assert.DoesNotContain("Michelle Brant", result);
        Assert.DoesNotContain("michelleb@cercanolp.com", result);
        // Only the three known PII fields are touched - a company/group name isn't a customer's own
        // identity and was never one of the fields this redaction targets.
        Assert.Contains("Cercano Management LLC", result);
    }

    // Live-found 2026-09-07: a genuine multi-sheet reservation's raw payload ran to 15,528 characters -
    // well past the original 8,000-character cap, cutting the diagnostic log off partway through the
    // FIRST sibling event, before a second or third could even appear (architecture doc D117). This
    // pins the actual reported case directly: a payload this size must now survive whole.
    [Fact]
    public void RedactAndTruncate_RealisticMultiSheetPayloadSize_IsNotTruncated()
    {
        var body = new string('a', 15_528);

        var result = BreelyBookingWebhookEndpoint.RedactAndTruncate(body);

        Assert.Equal(body, result);
        Assert.DoesNotContain("truncated", result);
    }

    [Fact]
    public void RedactAndTruncate_PayloadOverTheCap_TruncatesAndNamesTheRealLength()
    {
        var body = new string('a', BreelyBookingWebhookEndpoint.MaxRawPayloadLogLength + 500);

        var result = BreelyBookingWebhookEndpoint.RedactAndTruncate(body);

        Assert.Equal(BreelyBookingWebhookEndpoint.MaxRawPayloadLogLength, result.IndexOf("...[truncated,", StringComparison.Ordinal));
        Assert.Contains($"truncated, {body.Length} total chars", result);
    }

    [Fact]
    public void RedactAndTruncate_ExactlyAtTheCap_IsNotTruncated()
    {
        var body = new string('a', BreelyBookingWebhookEndpoint.MaxRawPayloadLogLength);

        var result = BreelyBookingWebhookEndpoint.RedactAndTruncate(body);

        Assert.Equal(body, result);
    }
}
