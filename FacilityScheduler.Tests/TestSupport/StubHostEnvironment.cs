using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>AppLogService takes an IHostEnvironment but never reads it when AppLog:LogDirectory is
/// set, which every test does.</summary>
public sealed class StubHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "FacilityScheduler.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
