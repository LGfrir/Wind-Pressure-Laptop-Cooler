param(
    [ValidateRange(1, 30)][int]$Samples = 5,
    [ValidateRange(100, 10000)][int]$IntervalMilliseconds = 2000,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'

# DSTS queries only. This tool has no firmware-setting, fan-control or INIT path.
if (-not ('SmartFanAsusReadOnlySensors' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class SmartFanAsusReadOnlySensors {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,uint inputLength,byte[] output,uint outputLength,out uint returned,IntPtr overlapped);
    public static uint ReadStatus(uint id) {
        using(var handle=CreateFile(@"\\.\ATKACPI",0xC0000000,3,IntPtr.Zero,3,0x80,IntPtr.Zero)) {
            if(handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot open ASUS ACPI device");
            var input=new byte[16]; var output=new byte[16];
            Array.Copy(BitConverter.GetBytes(0x53545344u),0,input,0,4);
            Array.Copy(BitConverter.GetBytes(8u),0,input,4,4);
            Array.Copy(BitConverter.GetBytes(id),0,input,8,4);
            uint returned;
            if(!DeviceIoControl(handle,0x0022240C,input,16,output,16,out returned,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"ASUS DSTS query failed");
            if(returned<4) throw new InvalidOperationException("ASUS DSTS reply shorter than four bytes");
            return BitConverter.ToUInt32(output,0);
        }
    }
}
'@
}
$taskAsusEndpoints = [ordered]@{
    CpuTemperatureCelsius = [uint32]0x00120094
    CpuFanRpm = [uint32]0x00110013
    GpuFanRpm = [uint32]0x00110014
    MiddleFanRpm = [uint32]0x00110031
}
$taskAsusReadings = @(
    for ($taskAsusSample = 0; $taskAsusSample -lt $Samples; $taskAsusSample++) {
        foreach ($taskAsusEndpoint in $taskAsusEndpoints.GetEnumerator()) {
            $taskAsusRaw = [SmartFanAsusReadOnlySensors]::ReadStatus($taskAsusEndpoint.Value)
            $taskAsusValue = $taskAsusRaw -band 0xFFFF
            [PSCustomObject]@{
                Timestamp = [DateTimeOffset]::Now.ToString('o')
                Name = $taskAsusEndpoint.Key
                Endpoint = '0x{0:X8}' -f $taskAsusEndpoint.Value
                Raw = '0x{0:X8}' -f $taskAsusRaw
                Present = ($taskAsusRaw -band 0x10000) -ne 0
                Value = $(if ($taskAsusEndpoint.Key -like '*FanRpm') { $taskAsusValue * 100 } else { $taskAsusValue })
            }
        }
        if ($taskAsusSample -lt $Samples - 1) { Start-Sleep -Milliseconds $IntervalMilliseconds }
    }
)
$taskAsusJson = ConvertTo-Json -InputObject $taskAsusReadings -Depth 3
if ($OutputPath) { Set-Content -LiteralPath $OutputPath -Value $taskAsusJson -Encoding utf8 }
$taskAsusJson
