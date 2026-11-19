param(
    [string[]]$Path,
    [string]$Output
)
$ErrorActionPreference = 'Stop'
if (-not $Path) {
    $projectRoot = Split-Path -Parent $PSScriptRoot
    $Path = @(
        (Join-Path $projectRoot 'artifacts\win-x64\SmartFan.exe'),
        (Join-Path $projectRoot 'artifacts\win-x64\SmartFan.Agent.exe'),
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    )
}

# 只读：没有调用附件管理器的 Prompt/Execute/Save，也不修改 ADS、ACL 或注册表。
if (-not ('SmartFanSecurityDiagnostics.NativeQueries' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace SmartFanSecurityDiagnostics
{
    [ComImport, Guid("79eac9ee-baf9-11ce-8c82-00aa004ba90b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInternetSecurityManager
    {
        [PreserveSig] int SetSecuritySite(IntPtr site);
        [PreserveSig] int GetSecuritySite(out IntPtr site);
        [PreserveSig] int MapUrlToZone([MarshalAs(UnmanagedType.LPWStr)] string url, out uint zone, uint flags);
    }
    [ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAttachmentExecute
    {
        [PreserveSig] int SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
        [PreserveSig] int SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
        [PreserveSig] int CheckPolicy();
    }
    public sealed class NativeResult
    {
        public string DosDevice { get; set; }
        public uint DriveType { get; set; }
        public uint? FileZone { get; set; }
        public string ZoneHResult { get; set; }
        public string AttachmentLocalOnlyPolicy { get; set; }
        public string AttachmentWithLocalSourcePolicy { get; set; }
        public string Error { get; set; }
    }
    public static class NativeQueries
    {
        [DllImport("urlmon.dll", PreserveSig = true)]
        private static extern int CoInternetCreateSecurityManager(IntPtr serviceProvider, out IInternetSecurityManager manager, uint reserved);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string name, StringBuilder target, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveType(string root);
        private static string HResult(int result) { return "0x" + unchecked((uint)result).ToString("X8"); }
        private static string AttachmentPolicy(string path, bool localSource)
        {
            object instance = null;
            try
            {
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4125dd96-e03a-4103-8f70-e0597d803b9c"), true));
                var attachment = (IAttachmentExecute)instance;
                int result = attachment.SetLocalPath(path);
                if (result < 0) return "SetLocalPath " + HResult(result);
                if (localSource)
                {
                    result = attachment.SetSource(new Uri(path).AbsoluteUri);
                    if (result < 0) return "SetSource " + HResult(result);
                }
                result = attachment.CheckPolicy();
                return HResult(result) + (result == 0 ? " Enable" : result == 1 ? " Prompt" : " Disable/Error");
            }
            finally { if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
        }
        public static NativeResult Inspect(string path)
        {
            var result = new NativeResult();
            try
            {
                string root = System.IO.Path.GetPathRoot(path);
                result.DriveType = GetDriveType(root);
                var target = new StringBuilder(2048);
                if (root.Length >= 2 && QueryDosDevice(root.Substring(0, 2), target, target.Capacity) > 0) result.DosDevice = target.ToString();
                IInternetSecurityManager manager;
                int hr = CoInternetCreateSecurityManager(IntPtr.Zero, out manager, 0);
                if (hr >= 0)
                {
                    try
                    {
                        uint zone;
                        // MUTZ_ISFILE: treat as file path.
                        hr = manager.MapUrlToZone(path, out zone, 0x00000002);
                        if (hr >= 0) result.FileZone = zone;
                    }
                    finally { Marshal.FinalReleaseComObject(manager); }
                }
                result.ZoneHResult = HResult(hr);
                result.AttachmentLocalOnlyPolicy = AttachmentPolicy(path, false);
                result.AttachmentWithLocalSourcePolicy = AttachmentPolicy(path, true);
            }
            catch (Exception error) { result.Error = error.GetType().Name + ": " + error.Message; }
            return result;
        }
    }
}
'@

$files = foreach ($target in $Path) {
    $resolved = (Resolve-Path -LiteralPath $target).ProviderPath
    $item = Get-Item -LiteralPath $resolved
    $signature = Get-AuthenticodeSignature -LiteralPath $resolved
    $zone = Get-Content -LiteralPath $resolved -Stream Zone.Identifier -Raw -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Path = $resolved
        Length = $item.Length
        Sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        SignatureStatus = $signature.Status.ToString()
        Streams = @(Get-Item -LiteralPath $resolved -Stream * | Select-Object Stream, Length)
        ZoneIdentifier = $zone
        Native = [SmartFanSecurityDiagnostics.NativeQueries]::Inspect($resolved)
    }
}
$report = [pscustomobject]@{
    Timestamp = [DateTimeOffset]::Now
    ReadOnly = $true
    Interpretation = 'CheckPolicy is a read-only evidence test, not proof of which UI a particular launcher displayed. Zone 0 is LocalMachine. S_OK=Enable, S_FALSE=Prompt. No file was launched or unblocked.'
    Files = @($files)
}
$json = $report | ConvertTo-Json -Depth 8
if ($Output) { $json | Set-Content -LiteralPath $Output -Encoding utf8 }
$json
