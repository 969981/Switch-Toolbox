param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $PublishDir).Path

# Names observed in the real 2026-11-06 successful win-x64 published
# dependency audit (run 42144446405). These are legacy managed copies,
# NOT the intentionally distinct x86/x64 native binaries.
# The canonical source is the DLL actually published at the application root,
# built against the migrated projects and PackageReferences.
$names = @(
    'AssimpNet.dll',
    'BarsLibrary.dll',
    'ByamlExt.dll',
    'CsvHelper.dll',
    'DirectXTexNet.dll',
    'EditorCoreCommon.dll',
    'LZ4.dll',
    'SARCExt.dll',
    'Syroot.Maths.dll',
    'Syroot.NintenTools.Bfres.dll',
    'Syroot.NintenTools.NSW.Bfres.dll',
    'Syroot.NintenTools.NSW.Bntx.dll'
)

$report = @()
foreach ($name in $names) {
    $canonical = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $canonical -PathType Leaf)) {
        throw "Required compiled/published dependency missing: $canonical"
    }
    $identity = [System.Reflection.AssemblyName]::GetAssemblyName($canonical)
    $sha = (Get-FileHash -LiteralPath $canonical -Algorithm SHA256).Hash.ToLowerInvariant()
    $variants = @()

    # Only overwrite historical copies already deployed at these search
    # locations. Do not generate new plugin-private DLLs or touch native folders.
    foreach ($relative in @("Lib/$name", "Lib/Plugins/$name")) {
        $target = Join-Path $root ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        $before = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($before -ne $sha) {
            Copy-Item -LiteralPath $canonical -Destination $target -Force
        }
        $after = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($after -ne $sha) { throw "Dependency normalization failed: $relative" }
        $variants += [pscustomobject]@{
            Path = $relative
            OriginalSha256 = $before
            FinalSha256 = $after
            WasReplaced = ($before -ne $after)
        }
        Write-Host ("NET10_DEP_PIN {0}: {1} -> {2}, replaced={3}" -f $name,
            $before.Substring(0,12), $after.Substring(0,12), ($before -ne $after))
    }
    $report += [pscustomobject]@{
        Name = $name
        ManagedIdentity = $identity.FullName
        CanonicalSha256 = $sha
        Variants = $variants
    }
}
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
}
Write-Host ("NET10_DEP_NORMALIZE names={0}, replaced={1}" -f
    $report.Count, @($report | ForEach-Object { $_.Variants } |
        Where-Object { $_.WasReplaced }).Count)
