namespace SmartFan.App.Services;

public enum TelemetryDataState { Waiting, Live, Expired, BackendDisconnected }

/// <summary>Display freshness is independent of hardware availability and the ESP connection.</summary>
public static class TelemetryFreshness
{
    public static TelemetryDataState Evaluate(DateTimeOffset? timestamp, DateTimeOffset now, int intervalMilliseconds, bool backendAvailable = true)
    {
        if (!backendAvailable) return TelemetryDataState.BackendDisconnected;
        if (timestamp is null) return TelemetryDataState.Waiting;
        var maximumAge = TimeSpan.FromMilliseconds(Math.Max(3000, Math.Clamp(intervalMilliseconds, 250, 60000) * 3L));
        return now - timestamp.Value > maximumAge || timestamp.Value - now > TimeSpan.FromSeconds(5)
            ? TelemetryDataState.Expired : TelemetryDataState.Live;
    }
    public static string Describe(TelemetryDataState state, DateTimeOffset? timestamp) => state switch
    {
        TelemetryDataState.Live => $"实时采集 · {timestamp:HH:mm:ss}",
        TelemetryDataState.Expired => $"数据已过期 · 最后更新 {timestamp:HH:mm:ss}",
        TelemetryDataState.BackendDisconnected => "后台连接中断 · 等待重新接入",
        _ => "等待硬件采样"
    };
}
