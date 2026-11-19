using System.Text;
using System.Text.Json;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;
using SmartFan.SelfTest;

Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Length == 0 || args.Contains("--offline"))
    {
        var assertions = ProtocolAssertions.Run();
        Console.WriteLine($"PASS：{assertions} 项离线协议断言通过。没有连接设备，也没有写入固件。");
        if (args.Length == 0 || args.All(item => item == "--offline")) return 0;
    }
    if (args.Contains("--help"))
    {
        Console.WriteLine("""
            SmartFan 原生 WinRT BLE 自检
              --offline                            离线协议测试（无参数时默认）
              --scan [--all]                       扫描 8 秒；--all 显示全部 BLE 设备
              --device <MAC 或 DeviceInformation.Id> 连接并只读检查 27 个特征，观察 10 秒 RPM
              --notify-seconds <1..120>             设置通知观察时长
              --report <路径.json>                  保存实际接口报告
              --reconnect                          只读检查后主动断开再重连一次
              --write <特征名> <十六进制>           显式写入，并读回验证
              --restore                            单项写入后恢复读取到的原值

            默认绝不写配置。--write 会改变实机，灯光配置会立即写入 flash。
            fan_pwm/fan_rpm 没有可靠原值读取，禁止和 --restore 同用。
            只读通过不代表风扇、灯珠、电源重启持久化等物理测试通过。
            """);
        return 0;
    }

    await using var client = new NativeBleClient { AutoReconnect = false };
    client.Diagnostic += (_, e) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {e.Message}{(e.Exception is null ? "" : " " + e.Exception.Message)}");
    if (args.Contains("--scan"))
    {
        var devices = await client.ScanAsync(TimeSpan.FromSeconds(8), !args.Contains("--all"), cancellation.Token);
        foreach (var device in devices) Console.WriteLine($"{device.Address} | RSSI {device.SignalStrength} | fan_svc={device.AdvertisesFanService} | {device.Name}");
        Console.WriteLine($"扫描结束，共 {devices.Count} 台。");
        if (Option("--device") is null) return devices.Count > 0 ? 0 : 2;
    }
    var target = Option("--device");
    if (target is null) throw new ArgumentException("请指定 --offline、--scan 或 --device；用 --help 查看命令。");
    await client.ConnectAsync(target, cancellation.Token);
    Console.WriteLine($"设备 {client.ConnectedDevice} | MTU {client.MaxPduSize} | 特征 {client.Characteristics.Count}");
    var rows = await InterfaceAudit.RunReadOnlyAsync(client, new Progress<InterfaceCheckResult>(PrintCheck), cancellation.Token);

    var writeIndex = Array.IndexOf(args, "--write");
    if (writeIndex >= 0)
    {
        if (writeIndex + 2 >= args.Length || !ProtocolCatalog.TryGet(args[writeIndex + 1], out var key)) throw new ArgumentException("--write 后需要有效特征名和十六进制载荷。");
        var hex = args[writeIndex + 2].Replace(" ", "").Replace("-", "");
        var payload = Convert.FromHexString(hex);
        PacketCodec.Validate(key, payload);
        byte[]? backup = null;
        if (args.Contains("--restore"))
        {
            if (key == CharacteristicKey.FanPwm || key == CharacteristicKey.FanRpm) throw new ArgumentException("PWM / 目标 RPM 无法读取可靠原值，不能自动恢复。");
            backup = await client.ReadAsync(key, cancellation.Token);
        }
        try
        {
            var result = await client.WriteAsync(key, payload, true, cancellation.Token);
            Console.WriteLine($"WRITE {ProtocolCatalog.Get(key).Name}: {result.Message}");
        }
        finally
        {
            // 用户取消时也尝试用新的有限时取消源恢复；恢复失败明确打印，不掩盖。
            if (backup is not null)
            {
                using var restoreCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var restored = await client.WriteAsync(key, backup, true, restoreCancellation.Token);
                Console.WriteLine($"RESTORE {ProtocolCatalog.Get(key).Name}: {restored.Message}");
            }
        }
    }
    var seconds = int.TryParse(Option("--notify-seconds"), out var configuredSeconds) ? configuredSeconds : 10;
    if (seconds is < 1 or > 120) throw new ArgumentException("通知观察时长必须在 1..120 秒。");
    var rpmCount = 0;
    client.RpmReceived += (_, e) => { Interlocked.Increment(ref rpmCount); Console.WriteLine($"NOTIFY fan_rpm = {e.Rpm} RPM ({e.Timestamp.ToLocalTime():HH:mm:ss.fff})"); };
    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellation.Token);
    Console.WriteLine($"RPM 观察 {seconds} 秒，共收到 {rpmCount} 条有效通知。开发板模拟 RPM 不代表真实测速。");
    if (args.Contains("--reconnect"))
    {
        await client.DisconnectAsync(cancellation.Token);
        await client.ConnectAsync(target, cancellation.Token);
        Console.WriteLine("主动断连再重连成功；通知已重新订阅。");
    }
    var reportPath = Option("--report");
    if (reportPath is not null)
    {
        var report = new
        {
            Timestamp = DateTimeOffset.Now,
            Device = client.ConnectedDevice,
            client.MaxPduSize,
            Mode = "ReadOnlyAuditUnlessExplicitWrite",
            NotificationSeconds = seconds,
            ValidNotifications = rpmCount,
            PhysicalHardwareVerified = false,
            Checks = rows.Select(row => new { row.Name, Status = row.Status.ToString(), row.Message, row.PayloadHex })
        };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
        Console.WriteLine($"报告已保存：{Path.GetFullPath(reportPath)}");
    }
    await client.DisconnectAsync(cancellation.Token);
    return rows.Any(row => row.Status == InterfaceCheckStatus.Failed) || rpmCount == 0 ? 1 : 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("操作已取消。"); return 130; }
catch (Exception ex) { Console.Error.WriteLine($"FAIL：{ex.Message}"); return 1; }

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
void PrintCheck(InterfaceCheckResult row) => Console.WriteLine($"{row.Status,-25} {row.Name,-23} {row.PayloadHex} | {row.Message}");
