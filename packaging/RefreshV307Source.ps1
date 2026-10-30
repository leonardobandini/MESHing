# Rebuild corresponding sources after recording verification, keeping tested binaries byte-identical.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$artifacts=Join-Path $root 'artifacts'
$candidate=Join-Path $artifacts 'candidate-v3.0.7'
$source=Join-Path $artifacts 'MESHing-3.0.7-source.zip'
$checksum=$source+'.sha256'
$target=Join-Path $candidate 'MESHing-3.0.7-source.zip'
$targetChecksum=$target+'.sha256'
$binary=Join-Path $artifacts 'MESHing-3.0.7.zip'
$alias=Join-Path $candidate 'MESHing-latest.zip'
$required=@($source,$checksum,$target,$targetChecksum,$binary,$alias)
if(-not(Test-Path -LiteralPath $artifacts -PathType Container) -or
   -not(Test-Path -LiteralPath $candidate -PathType Container) -or
   @($required | Where-Object {
       -not(Test-Path -LiteralPath $_ -PathType Leaf)
   }).Count){throw 'The verified 3.0.7 candidate and its source archive are required.'}
$oldHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$checksums=@([IO.File]::ReadAllText($checksum).TrimEnd("`r","`n"),
    [IO.File]::ReadAllText($targetChecksum).TrimEnd("`r","`n"))
if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $oldHash -or
   @($checksums|Where-Object {$_ -cne "$oldHash  MESHing-3.0.7-source.zip"}).Count){
    throw 'The existing corresponding sources differ from their verified checksums.'
}
$binaryHash=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
$aliasHash=(Get-FileHash -LiteralPath $alias -Algorithm SHA256).Hash
if($binaryHash -ne $aliasHash){throw 'Latest alias differs from tested versioned binary.'}
Remove-Item -LiteralPath $source,$checksum
try {
    & python (Join-Path $PSScriptRoot 'build-source-v2.py') '3.0.7'
    if($LASTEXITCODE -ne 0){throw 'Corresponding-source build failed.'}
    Copy-Item -LiteralPath $source -Destination $target -Force
    Copy-Item -LiteralPath $checksum -Destination $targetChecksum -Force
    if((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $binaryHash -or
       (Get-FileHash -LiteralPath $alias -Algorithm SHA256).Hash -ne $binaryHash){
        throw 'A tested installer changed while rebuilding only corresponding sources.'
    }
    "MESHING_307_SOURCE_REFRESH=PASS SHA256=$((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash)"
} catch {
    throw "Corresponding-source refresh failed; original verified source is still in $target : $_"
}
