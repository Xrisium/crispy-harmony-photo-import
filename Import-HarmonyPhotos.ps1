[CmdletBinding()]
param(
    [string]$Destination,
    [string]$Device,
    [ValidateSet('Hdc', 'Mtp')][string]$DeviceKind,
    [string[]]$Album = @('相机'),
    [switch]$AllAlbums,
    [switch]$ListAlbums,
    [switch]$DryRun,
    [switch]$NonInteractive,
    [string]$HdcPath,
    [string]$MtpAssembly,
    [string]$GuiRequest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $script:Utf8
$OutputEncoding = $script:Utf8
$script:Lock = $null
$script:LogWriter = $null
$script:Serial = ''
$script:PauseFile = ''
$mtp = $null

function Write-GuiEvent([string]$Kind, [hashtable]$Data) {
    if ($GuiRequest) {
        $Data['kind'] = $Kind
        [Console]::WriteLine('HARMONY_GUI ' + ($Data | ConvertTo-Json -Compress -Depth 5))
    }
}

function Test-ImportPause {
    if ($script:PauseFile -and [IO.File]::Exists($script:PauseFile)) {
        throw [OperationCanceledException]::new('导入已暂停。再次开始时会核验并跳过已完成的文件。')
    }
}

function Quote-WindowsArgument([string]$Value) {
    # CommandLineToArgvW/CRT quoting, including backslashes before a quote/end.
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Quote-ShellArgument([string]$Value) {
    return "'" + $Value.Replace("'", "'\''") + "'"
}

function Write-Event([string]$Message) {
    Write-Host $Message
    if ($script:LogWriter) {
        $script:LogWriter.WriteLine(('{0:o} {1}' -f (Get-Date), $Message))
        $script:LogWriter.Flush()
    }
}

function Invoke-Hdc([string[]]$CommandArgs) {
    $argsToUse = @($CommandArgs)
    $exe = $script:Hdc
    if ([IO.Path]::GetExtension($exe) -eq '.ps1') {
        # Explicit override, also used by the offline test device.
        $argsToUse = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $exe) + $argsToUse
        $exe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.Arguments = (($argsToUse | ForEach-Object { Quote-WindowsArgument $_ }) -join ' ')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $script:Utf8
    $info.StandardErrorEncoding = $script:Utf8
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    $started = $false
    try {
        [void]$process.Start()
        $started = $true
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit(1000)) { }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        # Some HDC/remote errors return exit code 0. Inspect their output too.
        if ($process.ExitCode -ne 0 -or $output -match '(?im)\[fail(?:ed)?\]|permission denied|no such file or directory|device not found|not match target|^error:|^ls:') {
            throw ('HDC 操作失败：' + $output.Trim())
        }
        return $output
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

function Invoke-PhoneShell([string]$Command) {
    return Invoke-Hdc -CommandArgs @('-t', $script:Serial, 'shell', $Command)
}

function Assert-SafeName([string]$Name) {
    if (-not $Name -or $Name -in @('.', '..') -or $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Name -match '[\x00-\x1f]' -or $Name -match '[. ]$' -or
        $Name -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
        throw "名称无法安全保存为 Windows 文件：$Name。请先在手机上重命名。"
    }
}

function Get-RemoteEntries([string]$Path) {
    $output = Invoke-PhoneShell ('ls -ln ' + (Quote-ShellArgument $Path))
    foreach ($line in ($output -split '\r?\n')) {
        if (-not $line.Trim() -or $line -match '^total\s+\d+\s*$') { continue }
        if ($line -notmatch '^(?<kind>[-dl])[rwxstST-]{9}\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}(?::\d{2})?)\s+(?<name>.+)$') {
            throw "无法解析手机文件列表，已停止以避免漏文件：$line"
        }
        $name = $Matches.name
        if ($name -in @('.', '..')) { continue }
        Assert-SafeName $name
        [pscustomobject]@{
            Kind = $Matches.kind; Name = $name; Size = [long]$Matches.size
            Modified = $Matches.date + ' ' + $Matches.time
            Remote = $Path.TrimEnd('/') + '/' + $name
        }
    }
}

function Get-AlbumFiles([string]$Remote, [string]$Relative) {
    foreach ($entry in @(Get-RemoteEntries $Remote)) {
        $relativePath = Join-Path $Relative $entry.Name
        if ($entry.Kind -eq 'd') {
            Get-AlbumFiles $entry.Remote $relativePath
        } elseif ($entry.Kind -eq '-') {
            [pscustomobject]@{ Remote = $entry.Remote; Relative = $relativePath; Size = $entry.Size; Modified = $entry.Modified }
        } else {
            throw "发现不支持的链接：$($entry.Remote)，已停止，未将它当作已导入文件。"
        }
    }
}

function Get-HdcTargets {
    if ($HdcPath) {
        $script:Hdc = (Resolve-Path -LiteralPath $HdcPath).Path
    } else {
        $toolDir = Join-Path $PSScriptRoot 'tools\hdc'
        $provenance = Get-Content -LiteralPath (Join-Path $toolDir 'provenance.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($file in $provenance.files) {
            if ((Get-LocalHash (Join-Path $toolDir $file.name)) -ne $file.sha256) { throw "随包工具校验失败：$($file.name)。请重新获取完整的 EXE。" }
        }
        $script:Hdc = Join-Path $toolDir 'hdc.exe'
    }
    $output = Invoke-Hdc -CommandArgs @('list', 'targets', '-v')
    foreach ($line in ($output -split '\r?\n')) {
        if ($line -match '^(?<id>\S+)\s+(?<transport>USB|TCP)\s+(?<state>\S+)') {
            [pscustomobject]@{ Id = $Matches.id; Name = $Matches.id; State = $Matches.state; Kind = 'Hdc' }
        }
    }
}

function Get-MtpAlbumFiles([string]$Id, [string]$Remote, [string]$Relative) {
    Test-ImportPause
    foreach ($entry in $mtp.GetEntries($Id)) {
        Test-ImportPause
        Assert-SafeName $entry.Name
        $remotePath = $Remote + '/' + $entry.Name
        $relativePath = Join-Path $Relative $entry.Name
        if ($entry.IsFolder) {
            Get-MtpAlbumFiles $entry.Id $remotePath $relativePath
        } else {
            [pscustomobject]@{ Remote = $remotePath; Relative = $relativePath; Size = $entry.Size; Modified = $entry.Modified; ObjectId = $entry.Id }
        }
    }
}

function Select-Phone {
    while ($true) {
        $errors = @()
        $allTargets = @(
            if ($DeviceKind -ne 'Mtp' -or ($GuiRequest -and $ListAlbums)) {
                try { Get-HdcTargets }
                catch { $errors += "鸿蒙检测失败。$_" }
            }
            if ($MtpAssembly -and ($DeviceKind -ne 'Hdc' -or ($GuiRequest -and $ListAlbums))) {
                try {
                    # The user already launched the EXE. Loading bytes also works from a USB drive or UNC path.
                    [void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $MtpAssembly).Path))
                    foreach ($phone in [CrispyHarmonyPhotoImport.MtpDevice]::ListPhones()) {
                        [pscustomobject]@{ Id = $phone.Id; Name = $phone.Name; State = 'Connected'; Kind = 'Mtp' }
                    }
                } catch { $errors += "USB 文件传输检测失败。$_" }
            }
        )
        foreach ($message in $errors) { Write-Host $message }
        $targets = @($allTargets | Where-Object { (!$Device -or $_.Id -eq $Device) -and (!$DeviceKind -or $_.Kind -eq $DeviceKind) })
        # Refresh can follow a reconnect or a change of phones. An import stays bound to its selection.
        if (!$targets.Count -and $GuiRequest -and $ListAlbums) { $targets = $allTargets; $Device = '' }
        $ready = @($targets | Where-Object { $_.State -eq 'Connected' })
        $selected = $null
        if ($targets.Count -eq 1) { $selected = $targets[0] }
        elseif (!$Device -and $ready.Count -eq 1) { $selected = $ready[0] }
        Write-GuiEvent 'devices' @{ items = $allTargets; selected = $selected }
        if ($selected -and $selected.State -eq 'Connected') { return $selected }
        if ($targets.Count -gt 1) {
            if ($NonInteractive) { throw '发现多台设备，请用 -Device 指定设备标识符。' }
            Write-Host '发现多台设备：'
            for ($i = 0; $i -lt $targets.Count; $i++) { Write-Host ("{0}. {1} ({2}, {3})" -f ($i + 1), $targets[$i].Name, $targets[$i].Kind, $targets[$i].State) }
            $choice = Read-Host '输入手机对应的序号'
            $number = 0
            if ([int]::TryParse($choice, [ref]$number) -and $number -ge 1 -and $number -le $targets.Count) {
                $Device = $targets[$number - 1].Id
                $DeviceKind = $targets[$number - 1].Kind
            }
            continue
        }
        Write-Host ''
        Write-Host '请用支持数据传输的数据线直连电脑，并解锁手机。'
        Write-Host '安卓手机在 USB 通知中选择「文件传输」。鸿蒙手机开启 USB 调试，并允许这台电脑。'
        if ($targets.Count) { Write-Host ("当前设备状态：" + $targets[0].State) }
        if ($NonInteractive) {
            if ($errors.Count -and !$allTargets.Count) { throw ($errors -join '；') }
            throw '选定的手机尚未连接或授权。请检查手机端设置后重新检测。'
        }
        $answer = Read-Host '完成后按回车重新检测；输入 Q 退出'
        if ($answer -eq 'q') { throw '已取消。' }
    }
}

function Get-LocalHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}

try {
    if ($GuiRequest) {
        $request = Get-Content -LiteralPath $GuiRequest -Raw -Encoding UTF8 | ConvertFrom-Json
        $NonInteractive = $true
        $ListAlbums = $request.action -eq 'browse'
        if (-not $ListAlbums -and $request.action -ne 'import') { throw '未知的窗口请求。' }
        $Device = $request.device
        if ($request.PSObject.Properties['deviceKind'] -and $request.deviceKind) { $DeviceKind = $request.deviceKind }
        $Destination = $request.destination
        $Album = @($request.album)
        $AllAlbums = $request.allAlbums
        $script:PauseFile = $GuiRequest + '.pause'
    }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
        throw '本便携包要求 64 位 Windows 10/11。'
    }
    Write-Host 'crispy-harmony-photo-import'
    Write-Host '将手机本地相册复制到电脑；保留手机原件。'
    if (!$MtpAssembly -and !$HdcPath) {
        $builtExe = Join-Path $PSScriptRoot 'dist\crispy-harmony-photo-import-win-x64.exe'
        if (Test-Path -LiteralPath $builtExe -PathType Leaf) { $MtpAssembly = $builtExe }
    }
    $phone = Select-Phone
    if ($phone.Kind -eq 'Mtp') {
        $script:Serial = 'MTP:' + $phone.Id
        Write-Host "已连接 USB 文件传输设备：$($phone.Name)"
        try {
            $mtp = [CrispyHarmonyPhotoImport.MtpDevice]::new($phone.Id)
            $albums = @(
                foreach ($folder in $mtp.GetAlbums()) {
                    Assert-SafeName $folder.Storage
                    Assert-SafeName $folder.Folder
                    if ($folder.Storage -eq '.harmony-import') { throw '存储名称与导入记录目录冲突：.harmony-import' }
                    [pscustomobject]@{
                        Id = $folder.Id; Name = "$($folder.Folder)（$($folder.Storage)）"; Camera = $folder.Folder -eq 'DCIM'
                        Remote = '/' + $folder.Storage + '/' + $folder.Folder
                        Relative = Join-Path $folder.Storage $folder.Folder
                    }
                }
            )
            if (@($albums | Group-Object Name | Where-Object { $_.Count -gt 1 }).Count) { throw '手机上有重名相册。' }
        } catch { throw "无法读取 USB 相册。请解锁手机，并在 USB 通知中选择「文件传输」。$_" }
    } else {
        $script:Serial = $phone.Id
        Write-Host "已连接鸿蒙手机：$script:Serial"
        # API 21+ media FUSE is the read interface exposed by HarmonyOS 6.
        $mediaRoot = '/mnt/data/100/media_fuse/Photo'
        try { $albums = @(Get-RemoteEntries $mediaRoot | Where-Object { $_.Kind -eq 'd' }) }
        catch { throw "当前系统未提供可读的相册接口，无法用此工具直接导入。请确认 HarmonyOS 6、USB 调试和授权状态。详细信息：$_" }
    }
    $camera = if ($mtp) { $albums | Where-Object Camera | Select-Object -First 1 }
        else { $albums | Where-Object { $_.Name -eq '相机' } | Select-Object -First 1 }
    $defaultAlbum = if ($camera) { $camera.Name } elseif ($albums.Count) { $albums[0].Name } else { $null }
    if ($ListAlbums) {
        Write-GuiEvent 'albums' @{ defaultAlbum = $defaultAlbum; items = @($albums | ForEach-Object { $_.Name }) }
        $albums | ForEach-Object { Write-Host $_.Name }
        exit 0
    }
    if (-not $NonInteractive -and -not $DryRun -and -not $AllAlbums -and
        -not $PSBoundParameters.ContainsKey('Album') -and $albums.Count) {
        Write-Host '手机上的相册：'
        for ($i = 0; $i -lt $albums.Count; $i++) { Write-Host ("{0}. {1}" -f ($i + 1), $albums[$i].Name) }
        while ($true) {
            $choice = (Read-Host "回车选择 [$defaultAlbum]，输入序号选择相册，A 选择全部，Q 退出").Trim()
            if (-not $choice) { $Album = @($defaultAlbum); break }
            if ($choice -eq 'q') { throw '已取消。' }
            if ($choice -eq 'a') { $AllAlbums = $true; break }
            $number = 0
            if ([int]::TryParse($choice, [ref]$number) -and $number -ge 1 -and $number -le $albums.Count) {
                $Album = @($albums[$number - 1].Name)
                break
            }
            Write-Host '请输入列表中的相册序号。'
        }
    }
    if ($mtp -and !$GuiRequest -and !$PSBoundParameters.ContainsKey('Album') -and ($NonInteractive -or $DryRun)) {
        $Album = if ($defaultAlbum) { @($defaultAlbum) } else { @() }
    }
    if ($AllAlbums) { $selected = @($albums) }
    else {
        foreach ($name in $Album) {
            if ($name -notin @($albums | ForEach-Object { $_.Name })) {
                throw "找不到相册[$name]。可用相册：$((@($albums | ForEach-Object { $_.Name })) -join '、')。使用 -ListAlbums 查看。"
            }
        }
        $selected = @($albums | Where-Object { $_.Name -in $Album })
    }
    $files = @(
        foreach ($a in $selected) {
            Test-ImportPause
            Write-Host "正在读取相册：$($a.Name)（大型相册可能需要一些时间）"
            Write-GuiEvent 'phase' @{ message = "正在读取相册 $($a.Name)…" }
            if ($mtp) { Get-MtpAlbumFiles $a.Id $a.Remote $a.Relative }
            else { Get-AlbumFiles $a.Remote $a.Name }
        }
    )
    Test-ImportPause
    if (@($selected | Where-Object { $_.Name -eq '.harmony-import' }).Count) { throw '相册名与导入记录目录冲突：.harmony-import' }
    $unique = @{}
    foreach ($file in $files) {
        if ($unique.ContainsKey($file.Relative)) { throw "Windows 路径发生重名冲突：$($file.Relative)" }
        $unique[$file.Relative] = $true
    }
    [long]$total = 0
    foreach ($file in $files) { $total += $file.Size }
    Write-Host ("本地可见文件：{0} 个，合计 {1:N2} GiB。" -f $files.Count, ($total / 1GB))
    Write-Host '云端独有、隐藏相册及回收站内容不包含在本次列表中。'
    if (-not $Destination) {
        $pictures = [Environment]::GetFolderPath('MyPictures')
        if (-not $pictures) { $pictures = Join-Path $env:USERPROFILE 'Pictures' }
        $date = Get-Date -Format 'yyyyMMdd'
        $default = Join-Path $pictures ($date + '-crispy-harmony-photo-import')
        $legacy = Join-Path $pictures ($date + '-huawei-export')
        if (-not (Test-Path -LiteralPath $default) -and (Test-Path -LiteralPath (Join-Path $legacy '.harmony-import') -PathType Container)) {
            $default = $legacy
        }
        if ($NonInteractive -or $DryRun) { $Destination = $default }
        else {
            $answer = Read-Host "保存位置（回车使用 $default）"
            $Destination = if ($answer.Trim()) { $answer.Trim().Trim('"') } else { $default }
        }
    }
    $Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
    Write-Host "保存位置：$Destination"
    if ($DryRun -or -not $files.Count) {
        if ($DryRun) { Write-Host '预览完成，未创建导出目录或复制文件。' }
        else {
            Write-Host '所选相册没有本地可见文件。'
            Write-GuiEvent 'empty' @{}
        }
        exit 0
    }
    if (-not $NonInteractive) {
        $answer = Read-Host '按回车开始导入；输入 Q 退出'
        if ($answer -eq 'q') { throw '已取消。' }
    }
    [void][IO.Directory]::CreateDirectory($Destination)
    $stateDir = Join-Path $Destination '.harmony-import'
    [void][IO.Directory]::CreateDirectory($stateDir)
    $script:Lock = [IO.File]::Open((Join-Path $stateDir 'import.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $oldSummary = Join-Path $stateDir 'summary.json'
    if (Test-Path -LiteralPath $oldSummary) { [IO.File]::Delete($oldSummary) }
    $script:LogWriter = New-Object IO.StreamWriter((Join-Path $stateDir 'import.log'), $true, $script:Utf8)
    $ownerPath = Join-Path $stateDir 'device.json'
    if (Test-Path -LiteralPath $ownerPath) {
        $owner = Get-Content -LiteralPath $ownerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($owner.schema -ne 1 -or $owner.device -ne $script:Serial) { throw '此目标目录属于另一台手机或不兼容的导入记录，请选择新的目录。' }
    } else {
        [IO.File]::WriteAllText($ownerPath, (@{ schema = 1; device = $script:Serial } | ConvertTo-Json), $script:Utf8)
    }
    $journal = Join-Path $stateDir 'completed.jsonl'
    $receipts = @{}
    if (Test-Path -LiteralPath $journal) {
        foreach ($line in [IO.File]::ReadAllLines($journal, $script:Utf8)) {
            try {
                $receipt = $line | ConvertFrom-Json
                $receipts[$receipt.remote] = $receipt
            } catch { Write-Event '忽略一条不完整的续传记录；对应文件将重新核验。' }
        }
        # Keep a truncated final line from swallowing the next valid record.
        $text = [IO.File]::ReadAllText($journal, $script:Utf8)
        if ($text -and -not $text.EndsWith("`n")) { [IO.File]::AppendAllText($journal, "`n", $script:Utf8) }
    }
    $pending = New-Object 'System.Collections.Generic.List[object]'
    $skipped = 0
    Write-GuiEvent 'phase' @{ message = '正在检查磁盘空间和已导入的文件…' }
    foreach ($file in $files) {
        Test-ImportPause
        $target = Join-Path $Destination $file.Relative
        if (Test-Path -LiteralPath $target) {
            $receipt = $receipts[$file.Remote]
            if (-not $receipt -or $receipt.relative -ne $file.Relative) {
                throw "目标中已有未被本工具记录的文件，已停止以避免覆盖：$target。请选择新的导入目录。"
            }
            $item = Get-Item -LiteralPath $target
            if (-not $item.PSIsContainer -and $item.Length -eq $file.Size -and $receipt.size -eq $file.Size -and
                $file.Modified -and $receipt.modified -eq $file.Modified -and (Get-LocalHash $target) -eq $receipt.sha256) {
                $skipped++
                continue
            }
        }
        $pending.Add($file)
    }
    [long]$needed = 0
    foreach ($file in $pending) { $needed += $file.Size }
    # Existing files remain in place until their replacement is validated.
    $drive = Get-PSDrive -PSProvider FileSystem | Where-Object {
        $Destination.StartsWith($_.Root, [StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object { $_.Root.Length } -Descending | Select-Object -First 1
    if ($drive -and $null -ne $drive.Free -and $drive.Free -lt ($needed + 64MB)) {
        throw ('目标磁盘空间不足：本次还需约 {0:N2} GiB，建议额外留出空间。' -f ($needed / 1GB))
    }
    $plan = @{ schema = 1; device = $script:Serial; createdAt = (Get-Date).ToString('o'); totalFiles = $files.Count; totalBytes = $total; files = $files }
    [IO.File]::WriteAllText((Join-Path $stateDir 'plan.json'), ($plan | ConvertTo-Json -Depth 5), $script:Utf8)
    Write-Event ("已核验并跳过 {0} 个文件；待导入 {1} 个。" -f $skipped, $pending.Count)
    $partial = Join-Path $stateDir 'current.partial'
    $copied = 0
    foreach ($file in $pending) {
        Test-ImportPause
        Write-GuiEvent 'progress' @{ done = ($copied + $skipped); total = $files.Count; file = $file.Relative }
        $index = $copied + 1
        Write-Progress -Activity '正在导入照片和视频' -Status "$index / $($pending.Count)  $($file.Relative)" -PercentComplete (100 * $copied / $pending.Count)
        Write-Event ("[{0}/{1}] {2}" -f $index, $pending.Count, $file.Relative)
        if (Test-Path -LiteralPath $partial) { [IO.File]::Delete($partial) }
        if ($mtp) { $mtp.CopyFile($file.ObjectId, $partial) }
        else {
            $output = Invoke-Hdc -CommandArgs @('-t', $script:Serial, 'file', 'recv', $file.Remote, $partial)
            if ($output -notmatch 'FileTransfer finish') { throw "未确认传输成功：$($file.Relative)。$output" }
        }
        if (-not [IO.File]::Exists($partial)) { throw "未确认传输成功：$($file.Relative)。" }
        if ((Get-Item -LiteralPath $partial).Length -ne $file.Size) { throw "文件大小核验失败：$($file.Relative)。未完成的文件保留在 $partial。" }
        $hash = Get-LocalHash $partial
        $target = Join-Path $Destination $file.Relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        $replaceExisting = Test-Path -LiteralPath $target
        if ($replaceExisting -and -not $receipts.ContainsKey($file.Remote)) {
            throw "传输期间出现新的同名文件，已保留该文件：$target"
        }
        $receipt = @{ remote = $file.Remote; relative = $file.Relative; size = $file.Size; modified = $file.Modified; sha256 = $hash; completedAt = (Get-Date).ToString('o') }
        [IO.File]::AppendAllText($journal, ($receipt | ConvertTo-Json -Compress) + "`n", $script:Utf8)
        # Record validated content before the atomic rename, so a crash between
        # rename and journal append cannot strand an unrecognized final file.
        if ($replaceExisting) { [IO.File]::Replace($partial, $target, [NullString]::Value) }
        else { [IO.File]::Move($partial, $target) }
        $copied++
    }
    Write-Progress -Activity '正在导入照片和视频' -Completed
    $summary = @{ schema = 1; status = 'complete'; device = $script:Serial; completedAt = (Get-Date).ToString('o'); totalFiles = $files.Count; totalBytes = $total; copied = $copied; verifiedSkipped = $skipped; verification = 'remote listing size + local SHA256; not remote SHA256 verification' }
    [IO.File]::WriteAllText((Join-Path $stateDir 'summary.json'), ($summary | ConvertTo-Json), $script:Utf8)
    Write-GuiEvent 'complete' @{ copied = $copied; skipped = $skipped; total = $files.Count; destination = $Destination }
    Write-Event "导入完成：新复制 $copied 个，核验后跳过 $skipped 个。保存位置：$Destination"
    Write-Event '每个文件已比对手机列表中的大小，并记录电脑端 SHA-256；未进行手机端哈希比对。'
    exit 0
} catch [System.OperationCanceledException] {
    Write-Event $_.Exception.Message
    Write-GuiEvent 'paused' @{}
    exit 2
} catch {
    Write-Host ''
    Write-Event ("未完成：" + $_.Exception.Message)
    Write-GuiEvent 'error' @{ message = $_.Exception.Message }
    Write-Event '修复提示的问题后，用相同目标目录重新运行即可续传。Ctrl+C 中断也可重新运行。'
    exit 1
} finally {
    if ($mtp) { $mtp.Dispose() }
    if ($script:LogWriter) { $script:LogWriter.Dispose() }
    if ($script:Lock) { $script:Lock.Dispose() }
}
