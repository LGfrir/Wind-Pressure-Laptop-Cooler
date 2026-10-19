using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SmartFan.Runtime;
using SmartFan.Runtime.Ipc;

/// <summary>Isolated local ACL/identity checks. No production pipe, hardware or UAC launch.</summary>
internal static class ElevatedPipeSecurityChecks
{
    public static async Task RunAsync(Action<bool, string> check, string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Missing test account SID.");
        var name = "SmartFan.SecurityIntegration." + Guid.NewGuid().ToString("N");
        var ble = new FakeBle(); var sampler = new FakeSampler();
        await using var host = new AgentHost(ble, sampler, new AgentConfigurationStore(Path.Combine(directory, "security-agent.json")));
        await using var server = new AgentPipeServer(name, host);
        await host.StartAsync(); await server.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
        {
            await raw.ConnectAsync(5000, timeout.Token);
            var security = raw.GetAccessControl();
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
            check(security.GetOwner(typeof(SecurityIdentifier))?.Equals(sid) == true,
                "IPC server owner is the current account User SID, independent of an elevated token's Owner group");
            check(security.AreAccessRulesProtected && rules.All(rule => !rule.IsInherited),
                "IPC uses an explicit protected DACL with no inherited broad permissions");
            check(rules.Where(rule => rule.AccessControlType == AccessControlType.Allow).All(rule => rule.IdentityReference.Equals(sid) || rule.IdentityReference.Equals(system)) &&
                  rules.Any(rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(sid) && (rule.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl),
                "only the current account and SYSTEM receive allow ACEs, with read-control available for peer validation");
            check(rules.Any(rule => rule.AccessControlType == AccessControlType.Deny && rule.IdentityReference.Equals(network) && (rule.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl),
                "NETWORK is explicitly denied, including remote connections authenticated as the same user");
        }
        await using (var client = new BackgroundClient(name))
        {
            await client.AttachAsync(timeout.Token);
            check(client.IsAttached && (await client.GetStatusAsync(timeout.Token)).ProcessId == Environment.ProcessId,
                "same-user IPC completes owner/DACL checks and server-side client identity verification");
            check((await client.EnumerateSensorsAsync(timeout.Token)).Any(sensor => sensor.Id == "test-temp"),
                "identification-only client SQOS permits authenticated requests without granting server impersonation rights");
        }
        check(ble.ConnectCalls == 0 && ble.Writes.IsEmpty, "security fixtures never connect or send device configuration");
        var trusted = TrustedSecurity(sid); Validate(trusted, sid);
        check(true, "peer validation accepts a protected current-user and SYSTEM-only security descriptor");
        var wrongOwner = TrustedSecurity(new SecurityIdentifier("S-1-5-21-111-222-333-444"));
        check(Rejected(wrongOwner, sid), "peer validation rejects a pipe owned by a different user SID");
        var inherited = TrustedSecurity(sid); inherited.SetAccessRuleProtection(false, true);
        check(Rejected(inherited, sid), "peer validation rejects an unprotected DACL");
        var broad = TrustedSecurity(sid); broad.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        check(Rejected(broad, sid), "peer validation rejects a same-owner pipe that permits Everyone");
        var remote = TrustedSecurity(sid);
        remote.RemoveAccessRuleSpecific(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        check(Rejected(remote, sid), "peer validation rejects a pipe that lacks the explicit remote-network denial");
    }

    // Optional root-owned CLI hook: --pipe-security-probe <isolated-pipe> <report-json>.
    // Start the existing --fake-agent fixture under the other token first; no real hardware is used.
    public static async Task ProbeExistingAsync(string pipeName, string outputPath)
    {
        if (!pipeName.StartsWith("SmartFan.SecurityIntegration.", StringComparison.Ordinal))
            throw new ArgumentException("The security probe is restricted to isolated fixture names.");
        using var identity = WindowsIdentity.GetCurrent();
        await using var client = new BackgroundClient(pipeName);
        await client.AttachAsync();
        var status = await client.GetStatusAsync();
        var sensors = await client.EnumerateSensorsAsync();
        var report = new
        {
            CapturedAt = DateTimeOffset.Now,
            ClientProcessId = Environment.ProcessId,
            ClientUserSid = identity.User?.Value,
            ClientOwnerSid = identity.Owner?.Value,
            ClientIsAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            ServerProcessId = status.ProcessId,
            ServerIsAdministrator = status.IsAdministrator,
            status.BootId,
            status.ConfigurationPath,
            Attached = client.IsAttached,
            SensorCount = sensors.Count,
            FakeSensorReceived = sensors.Any(sensor => sensor.Id == "test-temp"),
            status.IsConnected
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static PipeSecurity TrustedSecurity(SecurityIdentifier sid)
    {
        var security = new PipeSecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
    private static void Validate(PipeSecurity security, SecurityIdentifier sid) =>
        (typeof(BackgroundClient).GetMethod("ValidateServerSecurity", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException()).Invoke(null, [security, sid]);
    private static bool Rejected(PipeSecurity security, SecurityIdentifier sid)
    {
        try { Validate(security, sid); return false; }
        catch (TargetInvocationException exception) when (exception.InnerException is UnauthorizedAccessException) { return true; }
    }
}
