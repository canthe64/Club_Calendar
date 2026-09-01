using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

public class StaffSearchExportEndpointTests
{
    [Fact]
    public void ParseDate_ValidString_ReturnsIt()
    {
        Assert.Equal(new DateTime(2026, 9, 1), StaffSearchExportEndpoint.ParseDate("2026-09-01"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void ParseDate_MissingOrUnparsable_ReturnsNull(string? value)
    {
        Assert.Null(StaffSearchExportEndpoint.ParseDate(value));
    }
}
