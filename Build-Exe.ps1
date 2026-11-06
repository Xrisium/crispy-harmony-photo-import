$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
[void][IO.Directory]::CreateDirectory($dist)
$payload = Join-Path $dist 'exe-payload.zip'
$output = Join-Path $dist 'crispy-harmony-photo-import-win-x64.exe'
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'

try {
    Compress-Archive -LiteralPath @(
        (Join-Path $root 'Import-HarmonyPhotos.ps1'),
        (Join-Path $root 'LICENSE'),
        (Join-Path $root 'tools')
    ) -DestinationPath $payload -Force
    $references = @('System.IO.Compression.dll', 'System.Web.Extensions.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 @references `
        "/win32manifest:$root\gui\app.manifest" "/resource:$payload,crispy-harmony-photo-import.payload.zip" `
        "/out:$output" "$root\gui\CrispyHarmonyPhotoImport.cs"
    if ($LASTEXITCODE -ne 0) { throw "Windows executable build failed: $LASTEXITCODE" }
    $hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($output + '.sha256'), ($hash + '  ' + [IO.Path]::GetFileName($output) + "`n"))
    Write-Host $output
    Write-Host "SHA256: $hash"
} finally {
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
}
