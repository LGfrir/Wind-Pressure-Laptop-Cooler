using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using SmartFan.App.Models;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;
using SmartFan.Runtime;
using SmartFan.Runtime.Ipc;

if (args.Length >= 3 && args[0] == "--fake-agent")
{
    await AgentProcessFixture.RunAsync(args[1], args[2]);
    return;
}
if (args.Length >= 3 && args[0] == "--pipe-security-probe")
{
    await ElevatedPipeSecurityChecks.ProbeExistingAsync(args[1], args[2]);
    return;
}
await new IntegrationSuite().RunAsync();

internal sealed class IntegrationSuite
{
    private int _assertions;
    private string _directory = "";
    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _assertions++;
        Console.WriteLine($"PASS {message}");
    }
    public async Task RunAsync()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-runs"));
        _directory = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
        if (!_directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid cleanup path");
        Directory.CreateDirectory(_directory);
        try
        {
            await FramingAsync();
            await LogFilesAsync();
            await ElevatedPipeSecurityChecks.RunAsync(Check, _directory);
            await InProcessAsync();
            await SeparateProcessAsync();
            Console.WriteLine($"PASS: {_assertions} integration assertions");
        }
        finally { Directory.Delete(_directory, true); }
    }
    private async Task WaitForAsync(Func<bool> condition, string message, int timeoutMs = 6000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(message);
            await Task.Delay(30);
        }
    }
    private async Task<TException> ExpectAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try { await action(); }
        catch (TException ex) { _assertions++; Console.WriteLine($"PASS {message}"); return ex; }
        throw new InvalidOperationException(message);
    }
    private static AgentConfiguration TestConfiguration() => new()
    {
        AutoConnect = true, EnabledTelemetry = true,
        AutoUploadConfiguration = true,
        AutoUploadProfile = new DeviceProfile { Id = "integration-profile", Name = "Integration explicit opt-in", ManualPwm = 44 },
        SavedDevice = new BleDeviceInfo { Id = "integration-device", BluetoothAddress = 0x123456789ABC, Name = "Integration fake device" },
        Telemetry = new TelemetrySettings
        {
            Enabled = true, IntervalMs = 500, RamWireUnit = RamWireUnit.MiB,
            EnabledItems = new Dictionary<string, bool>
            {
                ["laptop_cpu_temp"] = true, ["laptop_gpu_temp"] = true,
                ["laptop_cpu_usage"] = true, ["laptop_tot_ram"] = true
            }
        }
    };
    private async Task FramingAsync()
    {
        await using var stream = new MemoryStream();
        await PipeFraming.WriteAsync(stream, PipeEnvelope.Create("request", "round-trip", "read", new ReadRequest { Key = CharacteristicKey.LightMode }), default);
        stream.Position = 0;
        var envelope = await PipeFraming.ReadAsync(stream, default);
        Check(envelope.Id == "round-trip" && envelope.Read<ReadRequest>().Key == CharacteristicKey.LightMode, "IPC framing preserves request identity and typed payload");
        foreach (int length in new[] { -1, 0, PipeFraming.MaximumFrameBytes + 1, int.MaxValue })
        {
            var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
            await ExpectAsync<InvalidDataException>(async () => await PipeFraming.ReadAsync(new MemoryStream(bytes), default), $"Reject invalid IPC frame length {length}");
        }
        await ExpectAsync<EndOfStreamException>(async () => await PipeFraming.ReadAsync(new MemoryStream(new byte[2]), default), "Truncated frame header fails promptly");
        await ExpectAsync<InvalidDataException>(() => PipeFraming.WriteAsync(new MemoryStream(), PipeEnvelope.Create("request", "oversized", "read", new string('x', PipeFraming.MaximumFrameBytes)), default), "Oversized serialized payload rejected before transmission");
    }
    private async Task LogFilesAsync()
    {
        string directory = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(directory);
        string stale = Path.Combine(directory, $"agent-{DateTime.Today.AddDays(-20):yyyyMMdd}.jsonl");
        string unrelated = Path.Combine(directory, "unrelated.jsonl");
        await File.WriteAllTextAsync(stale, "old");
        await File.WriteAllTextAsync(unrelated, "keep");
        var logger = new AgentFileLogger(directory, retentionDays: 14, maximumFileBytes: 512, maximumArchives: 2);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => logger.Write($"entry-{i} " + new string('x', 220)))));
        logger.Write(new string('中', 3000));
        Check(!File.Exists(stale), "Backend logger removes entries older than retention date");
        Check(File.Exists(unrelated), "Backend logger preserves unrelated files");
        var files = Directory.GetFiles(directory, "agent-*.jsonl");
        Check(files.Length <= 3, "Backend log rotation respects archive count cap");
        Check(files.All(path => new FileInfo(path).Length <= 512), "Backend log rotation and giant-message truncation respect byte cap");
        foreach (string path in files)
            foreach (string line in await File.ReadAllLinesAsync(path))
                using (JsonDocument.Parse(line)) { }
        Check(files.SelectMany(File.ReadAllLines).All(line => line.Contains("Timestamp") && line.Contains("Message")), "Concurrent rotated log records remain valid JSONL");
    }
    private async Task InProcessAsync()
    {
        string path = Path.Combine(_directory, "in-process-agent.json");
        var store = new AgentConfigurationStore(path);
        await store.SaveAsync(TestConfiguration());
        var ble = new FakeBle();
        var sampler = new FakeSampler();
        await using var host = new AgentHost(ble, sampler, store);
        int shutdownRequests = 0;
        host.ShutdownRequested += (_, _) => Interlocked.Increment(ref shutdownRequests);
        string pipeName = "SmartFan.Integration." + Guid.NewGuid().ToString("N");
        await using var server = new AgentPipeServer(pipeName, host);
        await host.StartAsync(); await server.StartAsync();
        await WaitForAsync(() => ble.ConnectCalls == 1 && sampler.ReadCount >= 2, "Host did not restore saved connection or sample");
        Check(host.GetConfiguration().EnabledTelemetry, "Agent restores telemetry option from startup JSON");
        Check(ble.IsConnected, "Agent restores saved device without a frontend");
        await WaitForAsync(() => ble.Writes.Count(key => key == CharacteristicKey.LightMode) == 1, "Opt-in profile was not applied");
        Check(ble.Writes.Count(key => key == CharacteristicKey.FanMode) == 1 && ble.Writes.Count(key => key == CharacteristicKey.FanPwm) == 1, "Backend applies explicitly opted-in supported profile once on physical connect");
        Check(!ble.Writes.Contains(CharacteristicKey.FanRpm), "Automatic profile does not replace ESP temperature control with PC target-RPM feedback");
        await WaitForAsync(() => ble.Writes.Any(w => w == CharacteristicKey.LaptopMemoryTotal), "Initial memory telemetry missing");
        var first = new BackgroundClient(pipeName);
        await first.AttachAsync();
        Check(first.IsConnected && first.LatestSnapshot?.IsDemo == false, "Frontend receives existing connection and latest real snapshot");
        var initial = await first.GetStatusAsync();
        Check(initial.ConfigurationPath == Path.GetFullPath(path), "Frontend receives actual running agent configuration path");
        var bootId = initial.BootId;
        var generation = initial.ConnectionGeneration;
        int readsAtAttach = sampler.ReadCount;
        int memoryWritesAtAttach = ble.Writes.Count(k => k == CharacteristicKey.LaptopMemoryTotal);
        uint? receivedRpm = null;
        first.RpmReceived += (_, rpm) => receivedRpm = rpm.Rpm;
        ble.EmitRpm(2468);
        await WaitForAsync(() => receivedRpm == 2468 && first.AgentStatus.LastRpm == 2468, "RPM event not delivered");
        Check(first.AgentStatus.LastRpm == 2468, "RPM notification and state replay agree");
        await first.DisposeAsync();
        await WaitForAsync(() => sampler.ReadCount >= readsAtAttach + 2, "Sampling stopped when frontend closed");
        Check(ble.IsConnected && ble.DisconnectCalls == 0, "Frontend disposal leaves physical BLE connected");
        Check(sampler.DisposeCalls == 0, "Frontend disposal does not dispose hardware collector");
        Check(shutdownRequests == 0, "Ordinary frontend disposal never requests backend shutdown");
        await using var second = new BackgroundClient(pipeName);
        await second.AttachAsync();
        var reopened = await second.GetStatusAsync();
        Check(reopened.BootId == bootId && reopened.ConnectionGeneration == generation, "Reopen attaches to same backend boot and physical connection");
        Check(reopened.SampleCount >= initial.SampleCount + 2 && reopened.LastRpm == 2468, "Reopen retains growing sample counter and last RPM");
        Check(ble.ConnectCalls == 1 && ble.Writes.Count(k => k == CharacteristicKey.LaptopMemoryTotal) == memoryWritesAtAttach, "Reopen neither reconnects BLE nor resets telemetry diff cache");
        Check(ble.Writes.Count(key => key == CharacteristicKey.LightMode) == 1, "Frontend reattach does not repeat opt-in profile upload");
        await using var third = new BackgroundClient(pipeName);
        await third.AttachAsync();
        await Task.WhenAll(second.EnumerateSensorsAsync(), third.EnumerateSensorsAsync());
        Check(sampler.MaximumConcurrentCalls == 1, "Multiple frontends share one serialized hardware collector");
        second.OperationTimeout = TimeSpan.FromMilliseconds(200);
        ble.BlockFanModeReads = true;
        var clock = Stopwatch.StartNew();
        var blocked = second.ReadAsync(CharacteristicKey.FanMode);
        var independent = await third.ReadAsync(CharacteristicKey.LightBrightness);
        Check(independent.SequenceEqual(new byte[] { 128 }), "Blocked request does not stall another frontend");
        await ExpectAsync<BleOperationException>(async () => await blocked, "Unresponsive operation has bounded IPC timeout");
        Check(clock.Elapsed < TimeSpan.FromSeconds(2), "IPC request timeout is bounded independently of backend lifetime");
        await WaitForAsync(() => ble.CancelledReads > 0, "Request cancellation did not reach backend");
        Check((await third.GetStatusAsync()).IsConnected, "Cancelling one request keeps backend connection alive");
        using (var malformed = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
        {
            await malformed.ConnectAsync(2000);
            await PipeFraming.ReadAsync(malformed, default); // 接收欢迎状态，避免故意不读的客户端先触发发送超时。
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, PipeFraming.MaximumFrameBytes + 1);
            try { await malformed.WriteAsync(header); await malformed.FlushAsync(); }
            catch (IOException) { /* 非法帧被后台立即断开也是预期结果。 */ }
            await Task.Delay(100);
        }
        Check((await third.GetStatusAsync()).IsConnected && sampler.ReadCount > readsAtAttach, "Malformed client is isolated from healthy frontend and collector");
        await third.SetDemoAsync(true);
        await ExpectAsync<BleOperationException>(() => third.WriteAsync(CharacteristicKey.FanPwm, new byte[] { 50 }), "Demo guard is enforced by backend write endpoint");
        await WaitForAsync(() => host.GetStatus().Snapshot?.IsDemo == true, "Demo snapshot not published");
        Check(!host.GetConfiguration().EnabledTelemetry, "Entering demo explicitly disables real telemetry");
        int writesAfterDemoSnapshot = ble.Writes.Count;
        await Task.Delay(600);
        Check(ble.Writes.Count == writesAfterDemoSnapshot, "Demo never sends simulated telemetry");
        await third.SetDemoAsync(false);
        var restored = await third.GetConfigurationAsync();
        Check(!restored.DemoMode && !restored.EnabledTelemetry, "Leaving demo does not silently re-enable telemetry");
        restored.Telemetry.IntervalMs = 2000; restored.AutoReconnect = false;
        await third.ConfigureAsync(restored);
        var disk = await store.LoadAsync();
        Check(disk.Telemetry.IntervalMs == 2000 && !disk.AutoReconnect, "Frontend configuration is persisted by agent");
        Check(await third.StopAgentAsync(), "Explicit backend stop returns acknowledgement");
        await WaitForAsync(() => shutdownRequests == 1, "Shutdown event was not raised after acknowledgement");
        Check(shutdownRequests == 1, "Explicit stop raises backend lifetime shutdown event once");
        await host.DisposeAsync();
        await host.DisposeAsync();
        Check(ble.DisposeCalls == 1 && sampler.DisposeCalls == 1, "Backend shutdown disposes physical resources exactly once");
    }
    private async Task SeparateProcessAsync()
    {
        string pipe = "SmartFan.ProcessIntegration." + Guid.NewGuid().ToString("N");
        string path = Path.Combine(_directory, "process-agent.json");
        await new AgentConfigurationStore(path).SaveAsync(TestConfiguration());
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--fake-agent"); start.ArgumentList.Add(pipe); start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start fixture backend");
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (ready != "READY") throw new InvalidOperationException($"Fixture failed to start: {ready}, {await process.StandardError.ReadToEndAsync()}");
            var frontA = new BackgroundClient(pipe);
            await frontA.AttachAsync();
            var initial = await frontA.GetStatusAsync();
            Check(initial.ProcessId == process.Id && initial.ProcessId != Environment.ProcessId, "Frontend talks to a genuinely separate backend process");
            await frontA.DisposeAsync();
            await Task.Delay(1200);
            Check(!process.HasExited, "Backend OS process survives frontend disposal");
            await using var frontB = new BackgroundClient(pipe);
            await frontB.AttachAsync();
            var reopened = await frontB.GetStatusAsync();
            Check(reopened.BootId == initial.BootId && reopened.SampleCount > initial.SampleCount, "New frontend sees uninterrupted collection in same process");
            var configuration = await frontB.GetConfigurationAsync();
            Check(configuration.EnabledTelemetry && configuration.AutoConnect, "Reopened frontend adopts persisted backend options");
            Check(await frontB.StopAgentAsync(), "Cross-process stop acknowledges before backend exits");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("stop");
                await process.StandardInput.FlushAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (TimeoutException) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
        Check(process.ExitCode == 0, "Separate backend exits cleanly only after explicit stop");
        string reportPath = path + ".report.json";
        var report = JsonSerializer.Deserialize<FixtureReport>(await File.ReadAllTextAsync(reportPath)) ?? throw new InvalidDataException("Missing fixture report");
        Check(report.ConnectCalls == 1 && report.DisconnectCalls == 0 && report.MaximumConcurrentCalls == 1, "Cross-process detach/reopen has one connection and one collector");
        Check(report.BleDisposeCalls == 1 && report.SamplerDisposeCalls == 1, "Cross-process physical cleanup happens at backend exit");
    }
}

internal sealed class FakeSampler : IHardwareSampler
{
    private int _readCount, _active, _maximum, _disposed;
    public int ReadCount => Volatile.Read(ref _readCount);
    public int MaximumConcurrentCalls => Volatile.Read(ref _maximum);
    public int DisposeCalls => Volatile.Read(ref _disposed);
    private void Enter()
    {
        int active = Interlocked.Increment(ref _active);
        int previous;
        do { previous = _maximum; if (previous >= active) break; } while (Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
    }
    public async Task<TelemetrySnapshot> ReadAsync(SensorPreferences? preferences = null, bool demo = false, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            await Task.Delay(20, cancellationToken);
            int count = Interlocked.Increment(ref _readCount);
            return new TelemetrySnapshot { CpuTemperature = 60, GpuTemperature = 70, CpuUsage = count % 100, RamTotalMiB = 32768, IsDemo = demo, Status = "Integration fake sample" };
        }
        finally { Interlocked.Decrement(ref _active); }
    }
    public async Task<IReadOnlyList<SensorDescriptor>> EnumerateSensorsAsync(CancellationToken cancellationToken = default)
    {
        Enter();
        try { await Task.Delay(30, cancellationToken); return new[] { new SensorDescriptor { Id = "test-temp", Name = "CPU Package", SensorType = "Temperature", Value = 60 } }; }
        finally { Interlocked.Decrement(ref _active); }
    }
    public ValueTask DisposeAsync() { Interlocked.Increment(ref _disposed); return ValueTask.CompletedTask; }
}

internal sealed class FakeBle : IBleClient
{
    private int _connectCalls, _disconnectCalls, _disposeCalls, _cancelledReads;
    public int ConnectCalls => Volatile.Read(ref _connectCalls);
    public int DisconnectCalls => Volatile.Read(ref _disconnectCalls);
    public int DisposeCalls => Volatile.Read(ref _disposeCalls);
    public int CancelledReads => Volatile.Read(ref _cancelledReads);
    public bool BlockFanModeReads { get; set; }
    public ConcurrentQueue<CharacteristicKey> Writes { get; } = new();
    public BleConnectionState State { get; private set; }
    public bool IsConnected { get; private set; }
    public ushort MaxPduSize => 256;
    public BleDeviceInfo? ConnectedDevice { get; private set; }
    public IReadOnlyList<DiscoveredCharacteristic> Characteristics { get; } = ProtocolCatalog.All.Select(definition => new DiscoveredCharacteristic
    { Definition = definition, Properties = "Integration", CanRead = definition.CanRead, CanWrite = true, CanNotify = definition.CanNotify }).ToList();
    public bool AutoReconnect { get; set; }
    public TimeSpan ReconnectInterval { get; set; }
    public TimeSpan OperationTimeout { get; set; }
#pragma warning disable CS0067
    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;
    public event EventHandler<BleConnectionEventArgs>? ConnectionChanged;
    public event EventHandler<RpmEventArgs>? RpmReceived;
    public event EventHandler<BleDiagnosticEventArgs>? Diagnostic;
#pragma warning restore CS0067
    public Task ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCalls); ConnectedDevice = device; IsConnected = true; State = BleConnectionState.Connected;
        ConnectionChanged?.Invoke(this, new BleConnectionEventArgs(State, "Integration fake connected"));
        return Task.CompletedTask;
    }
    public Task ConnectAsync(string addressOrId, CancellationToken cancellationToken = default) => ConnectAsync(new BleDeviceInfo { Id = addressOrId, BluetoothAddress = 1 }, cancellationToken);
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _disconnectCalls); IsConnected = false; State = BleConnectionState.Disconnected;
        ConnectionChanged?.Invoke(this, new BleConnectionEventArgs(State, "Integration fake disconnected"));
        return Task.CompletedTask;
    }
    public async Task<byte[]> ReadAsync(CharacteristicKey key, CancellationToken cancellationToken = default)
    {
        if (BlockFanModeReads && key == CharacteristicKey.FanMode)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Increment(ref _cancelledReads); throw; }
        }
        return key switch
        {
            CharacteristicKey.FanMode => PacketCodec.EncodeInt32(0),
            CharacteristicKey.LightMode => PacketCodec.EncodeInt32(2),
            CharacteristicKey.LightBrightness => new byte[] { 128 },
            _ => throw new NotSupportedException("Unused integration characteristic")
        };
    }
    public Task<WriteVerificationResult> WriteAsync(CharacteristicKey key, ReadOnlyMemory<byte> payload, bool verify = true, CancellationToken cancellationToken = default)
    {
        PacketCodec.Validate(key, payload.Span); Writes.Enqueue(key);
        return Task.FromResult(new WriteVerificationResult { Key = key, Status = verify ? WriteVerificationStatus.Verified : WriteVerificationStatus.NotRequested, Readback = verify ? payload.ToArray() : null });
    }
    public void EmitRpm(uint rpm) => RpmReceived?.Invoke(this, new RpmEventArgs(rpm, DateTimeOffset.Now));
    public Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan duration, bool filterFanService = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BleDeviceInfo>>([]);
    public Task<bool> UnpairAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public ValueTask DisposeAsync() { Interlocked.Increment(ref _disposeCalls); IsConnected = false; return ValueTask.CompletedTask; }
}

internal sealed class FixtureReport
{
    public int ConnectCalls { get; set; }
    public int DisconnectCalls { get; set; }
    public int BleDisposeCalls { get; set; }
    public int SamplerDisposeCalls { get; set; }
    public int MaximumConcurrentCalls { get; set; }
}
internal static class AgentProcessFixture
{
    public static async Task RunAsync(string pipe, string configPath)
    {
        var ble = new FakeBle();
        var sampler = new FakeSampler();
        var host = new AgentHost(ble, sampler, new AgentConfigurationStore(configPath));
        var server = new AgentPipeServer(pipe, host);
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ShutdownRequested += (_, _) => shutdown.TrySetResult();
        try
        {
            await host.StartAsync(); await server.StartAsync();
            Console.WriteLine("READY"); await Console.Out.FlushAsync();
            // Console.In 的同步包装可能让 ReadLineAsync 本身阻塞；放在线程池，避免夹具的 stdin 阻塞 RPC 停止流程。
            await Task.WhenAny(Task.Run(() => Console.In.ReadLine()), shutdown.Task).WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally
        {
            await server.DisposeAsync(); await host.DisposeAsync();
            await File.WriteAllTextAsync(configPath + ".report.json", JsonSerializer.Serialize(new FixtureReport
            {
                ConnectCalls = ble.ConnectCalls, DisconnectCalls = ble.DisconnectCalls,
                BleDisposeCalls = ble.DisposeCalls, SamplerDisposeCalls = sampler.DisposeCalls, MaximumConcurrentCalls = sampler.MaximumConcurrentCalls
            }));
        }
    }
}
