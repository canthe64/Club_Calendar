using FacilityScheduler.Services;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Services;

public class StaffAccessServiceTests
{
    private static (StaffAccessService Service, FakeGraphGroupGateway Gateway) Build()
    {
        var facility = TestFacility.Create();
        var gateway = new FakeGraphGroupGateway();
        var appLog = TestAppLog.Create();
        var service = new StaffAccessService(gateway, facility, appLog);
        return (service, gateway);
    }

    [Fact]
    public async Task IsStaffAsync_UserInTheConfiguredGroup_ReturnsTrue()
    {
        var (service, gateway) = Build();
        gateway.GroupMembers[TestFacility.StaffGroupId] = ["user-1"];

        Assert.True(await service.IsStaffAsync("user-1"));
    }

    [Fact]
    public async Task IsStaffAsync_UserNotInTheConfiguredGroup_ReturnsFalse()
    {
        var (service, gateway) = Build();
        gateway.GroupMembers[TestFacility.StaffGroupId] = ["user-1"];

        Assert.False(await service.IsStaffAsync("user-2"));
    }

    [Fact]
    public async Task IsStaffAsync_UserInADifferentGroup_ReturnsFalse()
    {
        // Guards against a bug where any group match (not specifically the configured staff group)
        // would be treated as staff.
        var (service, gateway) = Build();
        gateway.GroupMembers["some-other-group"] = ["user-1"];

        Assert.False(await service.IsStaffAsync("user-1"));
    }

    [Fact]
    public async Task IsStaffAsync_GraphCallThrows_FailsClosedRatherThanThrowing()
    {
        var (service, gateway) = Build();
        gateway.GroupMembers[TestFacility.StaffGroupId] = ["user-1"];
        gateway.ThrowOnCheck = true;

        var result = await service.IsStaffAsync("user-1");

        Assert.False(result);
    }
}
