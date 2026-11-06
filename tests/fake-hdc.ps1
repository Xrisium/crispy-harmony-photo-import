$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$config = Get-Content -LiteralPath $env:HARMONY_TEST_CONFIG -Raw -Encoding UTF8 | ConvertFrom-Json
$call = @($args)
if ($call[0] -eq '-v') { Write-Output 'Ver: test-device'; exit 0 }
if ($call[0] -eq 'list') {
    foreach ($target in $config.targets) { Write-Output ($target.id + "`tUSB`t" + $target.state + "`tlocalhost`thdc") }
    exit 0
}
if ($call[0] -ne '-t') { throw 'Missing explicit device selection' }
if ($call[2] -eq 'shell') {
    $command = $call[3]
    if (-not $command.StartsWith('ls -ln ')) { throw "Unexpected shell command: $command" }
    $path = $command.Substring(7)
    if (-not ($path.StartsWith("'") -and $path.EndsWith("'"))) { throw 'Unquoted shell path' }
    $path = $path.Substring(1, $path.Length - 2).Replace("'\''", "'")
    if ($config.mode -eq 'unsupported') { Write-Output 'ls: Permission denied'; exit 0 }
    if ($path -eq '/mnt/data/100/media_fuse/Photo') {
        foreach ($name in $config.albums) { Write-Output ("drwxrwxrwx 2 1000 1000 3440 2026-11-05 12:00 " + $name) }
    } else {
        foreach ($file in $config.files) {
            $parent = $file.remote.Substring(0, $file.remote.LastIndexOf('/'))
            if ($parent -ne $path) { continue }
            $name = $file.remote.Substring($file.remote.LastIndexOf('/') + 1)
            Write-Output ("-rw-rw-rw- 1 1000 1000 {0} 2026-11-05 12:00 {1}" -f $file.size, $name)
        }
        foreach ($dir in $config.directories) {
            if ($dir.parent -eq $path) { Write-Output ("drwxrwxrwx 2 1000 1000 3440 2026-11-05 12:00 " + $dir.name) }
        }
        if ($config.mode -eq 'malformed') { Write-Output 'unexpected file listing' }
    }
    exit 0
}
if ($call[2] -eq 'file' -and $call[3] -eq 'recv') {
    [IO.File]::AppendAllText($env:HARMONY_TEST_TRANSFERS, $call[4] + "`n")
    if ($env:HARMONY_TEST_TRANSFER_DELAY_MS) { Start-Sleep -Milliseconds ([int]$env:HARMONY_TEST_TRANSFER_DELAY_MS) }
    if ($config.mode -eq 'failzero') { Write-Output '[Fail] USB disconnected'; exit 0 }
    $file = @($config.files | Where-Object { $_.remote -eq $call[4] })[0]
    if ($config.mode -eq 'midfail' -and $file.remote.EndsWith('.mp4')) { Write-Output '[Fail] USB disconnected'; exit 0 }
    $bytes = [IO.File]::ReadAllBytes($file.source)
    if ($config.mode -eq 'wrongsize') { $bytes = [byte[]]@(1) }
    [IO.File]::WriteAllBytes($call[5], $bytes)
    Write-Output 'FileTransfer finish, File count = 1'
    exit 0
}
throw ('Unexpected HDC invocation: ' + ($call -join ' '))
