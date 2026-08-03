namespace FacilityScheduler;

public class AppLogOptions
{
    public const string SectionName = "AppLog";

    /// <summary>Absolute path where rotating log files (and the persisted log-level marker) are
    /// written. Left blank, falls back to App_Data/logs under the content root - fine for local
    /// dev, but on Azure App Service that folder is replaced on every redeploy/zip-deploy.
    /// Production should point this at a path outside the deployed app folder (see the deployment
    /// guide's Logging section).</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>How many days of rotated daily log files to keep before automatic deletion.</summary>
    public int RetentionDays { get; set; } = 30;
}
