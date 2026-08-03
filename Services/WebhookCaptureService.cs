using System.Collections.Concurrent;

namespace FacilityScheduler.Services;

/// <summary>
/// A throwaway diagnostic buffer for WebhookCaptureEndpoint - holds the last few raw requests sent
/// to the capture URL so staff can inspect exactly what an external system (e.g. a booking
/// platform's webhook) actually sends, before any real integration is built against it. Purely
/// in-memory and bounded (oldest entries drop off) - same "ephemeral only" posture as the app's
/// other caches; nothing here is ever treated as authoritative data.
/// </summary>
public class WebhookCaptureService
{
    private const int MaxCaptured = 25;
    private readonly ConcurrentQueue<CapturedWebhookRequest> _captured = new();

    public void Capture(CapturedWebhookRequest request)
    {
        _captured.Enqueue(request);
        while (_captured.Count > MaxCaptured && _captured.TryDequeue(out _))
        {
            // trim to MaxCaptured
        }
    }

    public List<CapturedWebhookRequest> GetRecent() => _captured.Reverse().ToList();

    public void Clear()
    {
        while (_captured.TryDequeue(out _))
        {
        }
    }
}

public record CapturedWebhookRequest(
    DateTime ReceivedAtUtc,
    string Method,
    string? RemoteIp,
    string? QueryString,
    string? ContentType,
    List<(string Name, string Value)> Headers,
    string Body);
