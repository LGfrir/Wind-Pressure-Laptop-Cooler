using System.Buffers.Binary;
using System.Text.Json;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;

if (args.Contains("--hardware"))
{
    await using var monitor = new HardwareMonitorService(Console.WriteLine);
    var sensors = await monitor.EnumerateSensorsAsync();
    var snapshot = await monitor.ReadAsync();
    Console.WriteLine($"Detected {sensors.Count} sensors");
    foreach (var sensor in sensors) Console.WriteLine($"{sensor.HardwareType} | {sensor.SensorType} | {sensor.HardwareName} | {sensor.Name} | {sensor.Value} | {sensor.Id}");
    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
}
else
{
    var suite = new ServiceTests();
    await suite.RunAsync();
}

internal sealed class ServiceTests
{
    private int _assertions;
    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _assertions++;
    }

    private void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { _assertions++; return; }
        throw new InvalidOperationException(message);
    }

    public async Task RunAsync()
    {
        var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-runs"));
        var directory = Path.GetFullPath(Path.Combine(testRoot, Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup path escapes test-runs");
        Directory.CreateDirectory(directory);
        try
        {
            await PersistenceAsync(directory);
            await WidgetLayoutChecks.RunAsync(directory, Check);
            Curves();
            Sensors();
            HardwareDiagnostics();
            CpuVoltageSelectionChecks.Run(Check);
            AsusEcSensors();
            await TelemetryAsync();
            Console.WriteLine($"PASS: {_assertions} service assertions");
        }
        finally { Directory.Delete(directory, true); }
    }

    private async Task PersistenceAsync(string directory)
    {
        var store = new SettingsStore(directory);
        var settings = new AppSettings();
        Check(SettingsValidator.Validate(settings).Count == 0, "Defaults must be valid");
        Check(!settings.MinimizeToTray, "New installation closes frontend process by default");
        settings.Appearance.ThemeName = "Cyber";
        settings.Connection.AutoUploadConfiguration = true;
        settings.Profiles[0].LightingPayloads["cfg_lt_static"] = new byte[] { 255, 0, 10 };
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        Check(loaded.Warnings.Count == 0, "Valid load must not warn");
        Check(loaded.Settings.Appearance.ThemeName == "Cyber", "Theme round trip");
        Check(loaded.Settings.Connection.AutoUploadConfiguration, "Opt-in sync round trip");
        Check(loaded.Settings.Profiles[0].LightingPayloads["cfg_lt_static"].SequenceEqual(new byte[] { 255, 0, 10 }), "Binary lighting round trip");
        settings.Appearance.ThemeName = "Graphite";
        await store.SaveAsync(settings);
        Check(File.Exists(store.SettingsPath + ".bak"), "Atomic replacement backup");
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "No temporary file remains");
        var backup = await store.ImportAsync(store.SettingsPath + ".bak");
        Check(backup.Appearance.ThemeName == "Cyber", "Backup is previous complete document");
        var export = Path.Combine(directory, "export.json");
        await store.ExportAsync(export, settings);
        Check((await store.ImportAsync(export)).Profiles.Count == 3, "Full export import");
        var profilePath = Path.Combine(directory, "profile.json");
        await store.ExportProfileAsync(profilePath, settings.Profiles[0]);
        Check((await store.ImportProfileAsync(profilePath)).Name == settings.Profiles[0].Name, "Scene export import");
        var clone = SettingsStore.Clone(settings);
        clone.Profiles[0].Curve[0].TargetRpm = 999;
        Check(settings.Profiles[0].Curve[0].TargetRpm != 999, "Clone is deep");
        settings.Appearance.ThemeName = "System";
        await store.SaveAsync(settings);
        Check((await store.LoadAsync()).Settings.Appearance.ThemeName == "System", "System theme save/load");
        await store.ExportAsync(export, settings);
        Check((await store.ImportAsync(export)).Appearance.ThemeName == "System", "System theme export/import");
        settings.MinimizeToTray = true;
        await store.SaveAsync(settings);
        Check((await store.LoadAsync()).Settings.MinimizeToTray, "Explicit tray preference survives save/load");
        var json = await File.ReadAllTextAsync(store.SettingsPath);
        await File.WriteAllTextAsync(store.SettingsPath, "{broken json");
        var corrupted = await store.LoadAsync();
        Check(corrupted.Warnings.Count == 1, "Corruption is surfaced");
        Check(await File.ReadAllTextAsync(store.SettingsPath) == "{broken json", "Corrupt original preserved during load");
        await File.WriteAllTextAsync(store.SettingsPath, json.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99"));
        Check((await store.LoadAsync()).Warnings.Count == 1, "Future schema is refused");
        await File.WriteAllTextAsync(store.SettingsPath, "{}");
        Check((await store.LoadAsync()).Warnings.Count == 1, "Missing schema is refused");
        settings.Profiles[0].LightingPayloads["cfg_lt_blink"] = new byte[] { 2, 1, 2 };
        Check(SettingsValidator.Validate(settings).Any(e => e.Contains("cfg_lt_blink")), "Import validates full lighting protocol");
        settings.Profiles[0].LightingPayloads.Remove("cfg_lt_blink");
        settings.Profiles[0].LightingPayloads["fan_pwm"] = new byte[] { 50 };
        Check(SettingsValidator.Validate(settings).Any(e => e.Contains("fan_pwm")), "Lighting payload cannot hide a fan command");
        settings.Profiles[0].LightingPayloads.Remove("fan_pwm");
        settings.Appearance.WallpaperCropX = 0.5;
        Check(SettingsValidator.Validate(settings).Count > 0, "Out-of-image crop rejected");
    }

    private void Curves()
    {
        var points = new List<FanCurvePoint> { new() { TemperatureCelsius = 30, TargetRpm = 1000 }, new() { TemperatureCelsius = 70, TargetRpm = 3000 } };
        Check(CurveService.EvaluateRpm(points, 20) == 1000, "Below curve endpoint");
        Check(CurveService.EvaluateRpm(points, 50) == 2000, "Linear RPM interpolation");
        Check(CurveService.EvaluateRpm(points, 90) == 3000, "Above curve endpoint");
        points[1].TemperatureCelsius = 30;
        Check(CurveService.Validate(points).Count > 0, "Duplicate temperatures rejected");
        Throws<ArgumentException>(() => CurveService.EvaluateRpm(points, 40), "Invalid curve must not evaluate");
        points[1].TemperatureCelsius = 70; points[1].TargetRpm = double.NaN;
        Check(CurveService.Validate(points).Count > 0, "NaN rejected");
        var snapshot = new TelemetrySnapshot { CpuTemperature = 70, GpuTemperature = 80 };
        Check(CurveService.SelectTemperature(snapshot, TemperatureSource.Highest) == 80, "Highest source");
        Check(CurveService.SelectTemperature(snapshot, TemperatureSource.Cpu) == 70, "CPU source");
        Check(CurveService.SelectTemperature(new TelemetrySnapshot { CpuTemperature = 50 }, TemperatureSource.Gpu) is null, "Missing requested source not silently replaced");
        Check(CurveService.SelectTemperature(new TelemetrySnapshot { IsDemo = true, CpuTemperature = 99 }, TemperatureSource.Highest) is null, "Demo cannot control");
    }

    private void Sensors()
    {
        SensorDescriptor Sensor(string id, string name, string hw, string type, double? value) => new()
        { Id = id, Name = name, HardwareId = hw, HardwareName = hw, HardwareType = type, SensorType = "Temperature", Value = value };
        var sensors = new List<SensorDescriptor>
        {
            Sensor("cpu-package", "CPU Package", "cpu", "Cpu", 60),
            Sensor("cpu-max", "Core Max", "cpu", "Cpu", 72),
            Sensor("cpu-vrm", "VRM", "cpu", "Cpu", 95),
            Sensor("gpu-core", "GPU Core", "gpu1", "GpuNvidia", 65),
            Sensor("gpu-hotspot", "GPU Hot Spot", "gpu1", "GpuNvidia", 105),
            Sensor("igpu-core", "GPU Core", "gpu2", "GpuIntel", 58)
        };
        var cpu = sensors.Where(s => s.HardwareType == "Cpu");
        Check(SensorSelection.SelectTemperature(cpu, true)?.Id == "cpu-package", "CPU whitelist prefers package, not VRM maximum");
        Check(SensorSelection.SelectTemperature(cpu, true, "cpu-max")?.Id == "cpu-max", "Manual sensor override");
        Check(SensorSelection.SelectTemperature(cpu, true, "gone") is null, "Unavailable manual sensor not replaced");
        Check(SensorSelection.SelectTemperature(sensors.Where(s => s.HardwareId == "gpu1"), false)?.Id == "gpu-core", "GPU hotspot excluded");
        Check(SensorSelection.SelectGpuId(sensors, null) == "gpu1", "Discrete GPU default");
        Check(SensorSelection.SelectGpuId(sensors, "gpu2") == "gpu2", "Manual GPU selection");
        var hybridCpu = new[]
        {
            Sensor("p-core", "P-Core #1", "cpu", "Cpu", 63),
            Sensor("e-core", "E-Core #1", "cpu", "Cpu", 67),
            Sensor("delta", "P-Core #1 Distance to TjMax", "cpu", "Cpu", 90)
        };
        Check(SensorSelection.SelectTemperature(hybridCpu, true)?.Id == "e-core", "Hybrid CPU hottest absolute core fallback");
        Check(SensorSelection.SelectTemperature(hybridCpu, true, "delta") is null, "Manual distance-to-TjMax cannot become absolute temperature");
        Check(SensorSelection.SelectTemperature(new[] { Sensor("delta", "Core #1 Distance to TjMax", "cpu", "Cpu", 90) }, true) is null, "Legacy delta temperature excluded");
        Check(SensorSelection.SelectTemperature(new[] { Sensor("nan", "CPU Package", "cpu", "Cpu", double.NaN) }, true) is null, "Nonfinite temperature excluded");
    }

    private void HardwareDiagnostics()
    {
        var cpu = new[] { new SensorDescriptor { Id = "package", HardwareType = "Cpu", Name = "CPU Package", SensorType = "Temperature" } };
        HardwareMonitorDiagnostics Environment(bool installed, bool accessible, int? error = null, bool admin = false) => new()
        { PawnIoInstalled = installed, PawnIoDeviceAccessible = accessible, PawnIoDeviceErrorCode = error, IsAdministrator = admin };
        TemperatureReadStatus Diagnose(HardwareMonitorDiagnostics environment, string? selectedId = null) =>
            CpuTemperatureDiagnostics.Evaluate(cpu, null, selectedId, environment);
        Check(Diagnose(Environment(false, false, 2)).Code == TemperatureReadCode.DriverMissing, "Missing driver diagnosed before privilege advice");
        Check(Diagnose(Environment(true, false, 2)).Code == TemperatureReadCode.DriverUnavailable, "Installed but missing device is a driver failure");
        Check(Diagnose(Environment(true, false, 5)).Code == TemperatureReadCode.PermissionRequired, "Unelevated access denial asks to elevate background");
        Check(Diagnose(Environment(true, false, 5, true)).Code == TemperatureReadCode.DriverUnavailable, "Elevated access denial is not labelled missing elevation");
        Check(Diagnose(Environment(true, true)).Code == TemperatureReadCode.SensorReadFailed, "Enumerated null temperature is not labelled unsupported CPU");
        Check(Diagnose(Environment(true, true), "gone").Code == TemperatureReadCode.SelectedSensorUnavailable, "Stale selected sensor gets actionable diagnosis");
        Check(Diagnose(Environment(true, true), "package").Code == TemperatureReadCode.SelectedSensorUnavailable, "Selected sensor with no reading explained");
        var valid = new SensorDescriptor { Id = "package", Name = "CPU Package", Value = 65 };
        Check(CpuTemperatureDiagnostics.Evaluate(cpu, valid, null, Environment(false, false, 2)).Code == TemperatureReadCode.Available,
            "Real reading takes precedence over install metadata");
        Check(CpuTemperatureDiagnostics.Evaluate(cpu, null, null, Environment(true, true), "Initialization failure").Code == TemperatureReadCode.InitializationFailed,
            "Initialization failure preserved");
        Check(CpuTemperatureDiagnostics.Evaluate([], null, null, Environment(true, true), samplingError: "CPU read failed").Code == TemperatureReadCode.SensorReadFailed,
            "CPU update error preserved even if enumeration produced no sensors");
        Check(CpuTemperatureDiagnostics.Evaluate([], null, null, Environment(true, true)).Code == TemperatureReadCode.NoCpuSensors, "Missing CPU enumeration distinguished");
        var unknown = new[] { new SensorDescriptor { Id = "other", SensorType = "Temperature", Name = "Other CPU Sensor", Value = 55 } };
        Check(CpuTemperatureDiagnostics.Evaluate(unknown, null, null, Environment(true, true)).Code == TemperatureReadCode.NoSupportedSensor,
            "Unknown sensor names require explicit selection");
        var snapshot = new TelemetrySnapshot { CpuTemperatureStatus = Diagnose(Environment(false, false, 2)), HardwareMonitor = Environment(false, false, 2) };
        var roundTrip = JsonSerializer.Deserialize<TelemetrySnapshot>(JsonSerializer.Serialize(snapshot))!;
        Check(roundTrip.CpuTemperatureStatus.Code == TemperatureReadCode.DriverMissing && roundTrip.HardwareMonitor?.PawnIoDeviceErrorCode == 2,
            "Snapshot diagnostics survive IPC JSON");
        Check(JsonSerializer.Deserialize<TelemetrySnapshot>("{}")!.CpuTemperatureStatus.Code == TemperatureReadCode.NotSampled,
            "Old snapshots without diagnostic fields remain compatible");
    }

    private void AsusEcSensors()
    {
        Check(AsusEcProtocol.IsSupportedMachine("ASUS", "ROG Strix G16 G615LRG"), "Only verified ASUS model enables vendor provider");
        Check(!AsusEcProtocol.IsSupportedMachine("Other", "ROG Strix G16 G615LRG"), "Non-ASUS machines keep standard sampler");
        Check(!AsusEcProtocol.IsSupportedMachine("ASUS", "Unverified Model"), "Unverified ASUS models keep standard sampler");
        Check(AsusEcProtocol.DecodeCpuTemperature(0x0001004C) == 76, "ASUS CPU endpoint has Celsius units");
        Check(AsusEcProtocol.DecodeFanRpm(0x00010036) == 5400, "ASUS fan raw 54 means 5400 RPM");
        Check(AsusEcProtocol.DecodeFanRpm(0x00010000) == 0, "Supported stopped fan retains real zero RPM");
        foreach (var raw in new uint[] { 0, uint.MaxValue, 0x0008004C, 0x0009004C, 0x0003004C })
        {
            Check(AsusEcProtocol.DecodeCpuTemperature(raw) is null, "Missing or unknown ASUS status cannot become CPU temperature");
            Check(AsusEcProtocol.DecodeFanRpm(raw) is null, "Missing or unknown ASUS status cannot become fan RPM");
        }
        Check(AsusEcProtocol.DecodeCpuTemperature(0x00010000) is null, "Zero firmware temperature is unavailable");
        Check(AsusEcProtocol.DecodeCpuTemperature(0x000100FF) is null, "Impossible firmware temperature rejected");
        Check(AsusEcProtocol.DecodeFanRpm(0x000100FF) is null, "Out-of-range firmware fan reading rejected");
        var firmware = new SensorDescriptor { Id = AsusEcProtocol.CpuTemperatureId, Name = "ASUS EC CPU Temperature", SensorType = "Temperature", HardwareType = "Cpu", Value = 76 };
        var package = new SensorDescriptor { Id = "lhm-package", Name = "CPU Package", SensorType = "Temperature", HardwareType = "Cpu", Value = 80 };
        Check(SensorSelection.SelectTemperature(new[] { firmware, package }, true)?.Id == "lhm-package", "Valid LHM CPU package takes precedence over vendor fallback");
        Check(SensorSelection.SelectTemperature(new[] { firmware, new SensorDescriptor { Id = "lhm-package", Name = "CPU Package", SensorType = "Temperature" } }, true)?.Id == firmware.Id,
            "Vendor CPU endpoint supplies actual fallback when LHM has no temperature");
        Check(SensorSelection.SelectTemperature(new[] { firmware, package }, true, "lhm-package")?.Id == "lhm-package", "Manual LHM selection remains authoritative");
        var fans = new[]
        {
            new SensorDescriptor { Id = "middle", SensorType = "Fan", FanSlot = 2, Value = 4900 },
            new SensorDescriptor { Id = "cpu", SensorType = "Fan", FanSlot = 0, Value = 0 },
            new SensorDescriptor { Id = "arbitrary-lhm-fan", SensorType = "Fan", Value = 1200 }
        };
        var slots = SensorSelection.SelectFanSlots(fans);
        Check(slots.Count == 5 && slots[0]?.Id == "cpu" && slots[1] is null && slots[2]?.Id == "middle", "Vendor fan slots remain stable when GPU fan is unavailable");
        Check(slots[0]?.Value == 0, "Stopped CPU fan does not disappear");
        Check(slots.Count(s => s is not null) == 2, "Duplicate LHM fans are not appended to known vendor slots");
        Check(!SensorSelection.HasReliableFanPresence(slots), "Unknown vendor fan slot cannot be reported as absent");
        var confirmedSlots = SensorSelection.SelectFanSlots(fans.Append(new SensorDescriptor { Id = "gpu", SensorType = "Fan", FanSlot = 1, Value = null }));
        Check(SensorSelection.HasReliableFanPresence(confirmedSlots) && confirmedSlots[1]?.Value is null,
            "Confirmed installed fan with failed RPM read remains present without a fake zero");
        var fallbackSlots = SensorSelection.SelectFanSlots(new[] { fans[2], new SensorDescriptor { Id = "a-lhm-fan", SensorType = "Fan", Value = 2200 } });
        Check(fallbackSlots[0]?.Id == "a-lhm-fan" && fallbackSlots[1]?.Id == "arbitrary-lhm-fan", "Standard LHM fan ordering unchanged without vendor mapping");
        Check(SensorSelection.HasReliableFanPresence(fallbackSlots), "Detected standard fans retain their existing presence semantics");
    }

    private async Task TelemetryAsync()
    {
        var settings = new TelemetrySettings { Enabled = true };
        settings.EnabledItems["laptop_fan_used"] = true;
        settings.EnabledItems["laptop_fan_speed"] = true;
        var snapshot = new TelemetrySnapshot
        {
            CpuTemperature = 60, GpuTemperature = 70, CpuUsage = 20,
            RamUsedMiB = 8192, RamTotalMiB = 32768,
            FanPresenceAvailable = true, FanPresent = new double[] { 1, 1, 1, 0, 0 },
            FanRpm = new double?[] { 1000, 0, 2000, null, null }
        };
        var payloads = TelemetryUploader.BuildPayloads(snapshot, settings, out var notes);
        Check(!payloads.ContainsKey(CharacteristicKey.LaptopMemoryUsed) && notes.Count > 0, "Unknown memory wire unit is never guessed");
        Check(payloads[CharacteristicKey.LaptopFanUsed].Length == 40, "Five present flags encoded");
        Check(PacketCodec.DecodeFanArray(payloads[CharacteristicKey.LaptopFanUsed]).SequenceEqual(new double[] { 1, 1, 1, 0, 0 }), "Presence means installed, including stopped fan");
        Check(PacketCodec.DecodeFanArray(payloads[CharacteristicKey.LaptopFanSpeed])[1] == 0, "Actual stopped fan RPM remains zero");
        settings.RamWireUnit = RamWireUnit.Bytes;
        payloads = TelemetryUploader.BuildPayloads(snapshot, settings, out _);
        Check(PacketCodec.DecodeDouble(payloads[CharacteristicKey.LaptopMemoryUsed]) == 8192d * 1048576, "Explicit RAM bytes conversion");
        Check(!payloads.ContainsKey(CharacteristicKey.LaptopCpuVoltage), "Missing voltage is not fake zero");
        var vidPayloads = TelemetryUploader.BuildPayloads(new TelemetrySnapshot { CpuVoltage = 1.2, CpuVoltageKind = CpuVoltageKind.Vid, CpuTemperature = 61 }, settings, out var vidNotes);
        Check(!vidPayloads.ContainsKey(CharacteristicKey.LaptopCpuVoltage) && vidPayloads.ContainsKey(CharacteristicKey.LaptopCpuTemperature) && vidNotes.Any(note => note.Contains("VID")),
            "requested VID stays local and never masquerades as measured voltage on the existing firmware interface");
        var vcorePayloads = TelemetryUploader.BuildPayloads(new TelemetrySnapshot { CpuVoltage = 1.05, CpuVoltageKind = CpuVoltageKind.VCore }, settings, out _);
        Check(PacketCodec.DecodeDouble(vcorePayloads[CharacteristicKey.LaptopCpuVoltage]) == 1.05,
            "a valid measured VCore retains its existing voltage telemetry encoding");
        var voltageRoundTrip = JsonSerializer.Deserialize<TelemetrySnapshot>(JsonSerializer.Serialize(new TelemetrySnapshot { CpuVoltage = 1.2, CpuVoltageKind = CpuVoltageKind.Vid }))!;
        Check(voltageRoundTrip.CpuVoltage == 1.2 && voltageRoundTrip.CpuVoltageKind == CpuVoltageKind.Vid,
            "voltage semantics survive snapshot serialization instead of silently losing the VID distinction");
        var missingCpu = TelemetryUploader.BuildPayloads(new TelemetrySnapshot
        {
            GpuTemperature = 63,
            CpuTemperatureStatus = new TemperatureReadStatus { Code = TemperatureReadCode.DriverMissing, Message = "Missing PawnIO" }
        }, settings, out _);
        Check(!missingCpu.ContainsKey(CharacteristicKey.LaptopCpuTemperature) && missingCpu.ContainsKey(CharacteristicKey.LaptopGpuTemperature),
            "CPU dependency failure suppresses only missing CPU temperature, not valid GPU data");
        var missingFan = new TelemetrySnapshot { FanPresenceAvailable = true, FanPresent = new double[] { 1, 0, 0, 0, 0 } };
        Check(!TelemetryUploader.BuildPayloads(missingFan, settings, out _).ContainsKey(CharacteristicKey.LaptopFanSpeed), "Unknown installed fan speed not sent as zero");
        Check(TelemetryUploader.BuildPayloads(new TelemetrySnapshot { IsDemo = true, CpuTemperature = 99 }, settings, out _).Count == 0, "Demo packet guard");
        var fake = new FakeBleClient();
        var uploader = new TelemetryUploader(fake);
        var first = await uploader.SendAsync(snapshot, settings);
        Check(first.Written == payloads.Count, "First connection sends every available enabled metric");
        var second = await uploader.SendAsync(snapshot, settings);
        Check(second.Written == 0 && second.Unchanged == payloads.Count, "Unchanged metrics skipped");
        uploader.ResetCache();
        Check((await uploader.SendAsync(snapshot, settings)).Written == payloads.Count, "Reconnect resends constants");
        var before = fake.Writes.Count;
        await uploader.SendAsync(new TelemetrySnapshot { IsDemo = true, CpuTemperature = 80 }, settings);
        Check(fake.Writes.Count == before, "Demo never reaches BLE");
        fake.FailNext = true;
        uploader.ResetCache();
        try { await uploader.SendAsync(snapshot, settings); throw new InvalidOperationException("Expected write failure"); }
        catch (IOException) { _assertions++; }
        Check((await uploader.SendAsync(snapshot, settings)).Written == payloads.Count, "Failed write never cached");
        Check(fake.Writes.All(key => key >= CharacteristicKey.LaptopCpuUsage), "Telemetry never sends PWM/RPM controls");
    }
}

internal sealed class FakeBleClient : IBleClient
{
    public List<CharacteristicKey> Writes { get; } = [];
    public bool FailNext { get; set; }
    public BleConnectionState State => BleConnectionState.Connected;
    public bool IsConnected => true;
    public ushort MaxPduSize => 256;
    public BleDeviceInfo? ConnectedDevice => null;
    public IReadOnlyList<DiscoveredCharacteristic> Characteristics => [];
    public bool AutoReconnect { get; set; }
    public TimeSpan ReconnectInterval { get; set; }
    public TimeSpan OperationTimeout { get; set; }
#pragma warning disable CS0067
    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;
    public event EventHandler<BleConnectionEventArgs>? ConnectionChanged;
    public event EventHandler<RpmEventArgs>? RpmReceived;
    public event EventHandler<BleDiagnosticEventArgs>? Diagnostic;
#pragma warning restore CS0067
    public Task<WriteVerificationResult> WriteAsync(CharacteristicKey key, ReadOnlyMemory<byte> payload, bool verify = true, CancellationToken cancellationToken = default)
    {
        if (FailNext) { FailNext = false; throw new IOException("Simulated transport failure"); }
        PacketCodec.Validate(key, payload.Span); Writes.Add(key);
        return Task.FromResult(new WriteVerificationResult { Key = key, Status = WriteVerificationStatus.NotRequested });
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan duration, bool filterFanService = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ConnectAsync(string addressOrId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<byte[]> ReadAsync(CharacteristicKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> UnpairAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
