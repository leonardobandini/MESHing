# SPDX-License-Identifier: GPL-2.0-or-later
# Download the official prerequisite temporarily; verify version/signature, never run it.
$ErrorActionPreference = 'Stop'
$parent = Join-Path $env:TEMP 'opencode'
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Expected safe temporary parent.' }
$directory = Join-Path $parent ('femesh-vc-redist-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $file = Join-Path $directory 'vc_redist.x64.exe'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $web = New-Object Net.WebClient
    try { $web.DownloadFile('https://aka.ms/vc14/vc_redist.x64.exe', $file) }
    finally { $web.Dispose() }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    $version = (Get-Item -LiteralPath $file).VersionInfo.FileVersion
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)' -or
        [Version]$version -lt [Version]'14.42.34226.3') {
        throw "Unexpected Microsoft prerequisite: signature=$($signature.Status), version=$version"
    }
    "MICROSOFT_DOWNLOAD=PASS AUTHENTICODE=VALID VERSION=$version BYTES=$((Get-Item -LiteralPath $file).Length) EXECUTED=NO"
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
