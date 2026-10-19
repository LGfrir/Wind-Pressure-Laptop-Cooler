using SmartFan.App.Models;

namespace SmartFan.Runtime;

public interface IHardwareSampler : IAsyncDisposable
{
    Task<TelemetrySnapshot> ReadAsync(SensorPreferences? preferences = null, bool demo = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SensorDescriptor>> EnumerateSensorsAsync(CancellationToken cancellationToken = default);
}
