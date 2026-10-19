param(
    [string]$Runtime = 'win-x64',
    [switch]$Deploy  # 同时部署到 %LOCALAPPDATA%\SmartFanNext\app（该位置不弹 Windows 安全警告）
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $projectRoot "artifacts\$Runtime"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
dotnet publish (Join-Path $projectRoot 'SmartFan.App\SmartFan.App.csproj') -c Release -r $Runtime --self-contained true -p:SkipAgentCopy=true -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw '控制面板发布失败。' }
dotnet publish (Join-Path $projectRoot 'SmartFan.Agent\SmartFan.Agent.csproj') -c Release -r $Runtime --self-contained true -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw '后台发布失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $outputDirectory -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\agent.example.json') -Destination $outputDirectory -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $outputDirectory -Recurse -Force
$dependencyNote = Join-Path $projectRoot 'dependencies\PawnIO\README.md'
if (Test-Path -LiteralPath $dependencyNote) {
    $dependencyNoteDirectory = Join-Path $outputDirectory 'dependencies\PawnIO'
    New-Item -ItemType Directory -Path $dependencyNoteDirectory -Force | Out-Null
    Copy-Item -LiteralPath $dependencyNote -Destination (Join-Path $dependencyNoteDirectory 'README.md') -Force
}
$portableLauncher = @'
@echo off
start "" "%~dp0SmartFan.exe" --data-dir "%~dp0data"
'@
Set-Content -LiteralPath (Join-Path $outputDirectory 'Start-SmartFan-Portable.cmd') -Value $portableLauncher -Encoding ascii
Write-Output "发布完成：$outputDirectory。SmartFan.exe 使用用户配置目录；Start-SmartFan-Portable.cmd 使用包内 data 目录。"

# G 盘被附件管理器判为 Internet 区（CheckPolicy=Prompt），从 artifacts 直接启动会弹
# “打开文件-安全警告”。部署到用户本地目录后 FileZone=0、CheckPolicy=Enable，实测无弹窗。
if ($Deploy) {
    $deployDirectory = Join-Path $env:LOCALAPPDATA 'SmartFanNext\app'
    New-Item -ItemType Directory -Path $deployDirectory -Force | Out-Null
    $changedFiles = @(foreach ($sourceFile in Get-ChildItem -LiteralPath $outputDirectory -Recurse -File) {
        $relativePath = [IO.Path]::GetRelativePath($outputDirectory, $sourceFile.FullName)
        # 只部署程序与文档；用户配置、日志和便携数据不属于发布文件。
        if ($relativePath -match '^(data|\.local-data|frontend-logs|logs)[\\/]' -or $relativePath -in @('settings.json', 'agent.json')) { continue }
        $destinationPath = Join-Path $deployDirectory $relativePath
        if ((Test-Path -LiteralPath $destinationPath) -and (Get-FileHash -LiteralPath $sourceFile.FullName).Hash -eq (Get-FileHash -LiteralPath $destinationPath).Hash) { continue }
        [pscustomobject]@{ Source = $sourceFile.FullName; Destination = $destinationPath; RelativePath = $relativePath }
    })
    # 先检查全部变更再复制，避免更新到一半才碰到运行中的 DLL。
    $runningApplications = @(Get-Process -Name SmartFan,SmartFan.Agent -ErrorAction SilentlyContinue | Where-Object {
        # 普通部署进程可能无法读取管理员后台的路径；未知路径也必须先按占用处理。
        -not $_.Path -or [IO.Path]::GetDirectoryName($_.Path).Equals($deployDirectory, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($application in $runningApplications) {
        $inUse = @($changedFiles | Where-Object {
            $fileName = [IO.Path]::GetFileName($_.RelativePath)
            if ($application.ProcessName -eq 'SmartFan') { $fileName -match '^SmartFan\.(exe|dll|deps\.json|runtimeconfig\.json)$' }
            else { ($fileName -match '\.dll$' -and $fileName -ne 'SmartFan.dll') -or $fileName -match '^SmartFan\.Agent\.(exe|deps\.json|runtimeconfig\.json)$' }
        })
        if ($inUse.Count -gt 0) { throw "请先关闭 $($application.ProcessName)（PID $($application.Id)）再部署；当前未复制任何变更。" }
    }
    foreach ($file in $changedFiles) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($file.Destination)) -Force | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $file.Destination -Force
    }
    Write-Output "已部署：$deployDirectory（从此处启动不弹安全警告）。"
}
