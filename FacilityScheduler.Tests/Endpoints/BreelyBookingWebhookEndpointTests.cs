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
}
