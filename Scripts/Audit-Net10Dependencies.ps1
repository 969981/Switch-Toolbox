param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PublishDir)) { throw "Published directory not found: $PublishDir" }
$root = (Resolve-Path $PublishDir).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
$dlls = @(Get-ChildItem -Path $root -Recurse -File -Filter *.dll | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length).TrimStart([IO.Path]::DirectorySeparatorChar) -replace '\\','/'
    $assembly = $null
    try {
        $name = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName)
        $assembly = [ordered]@{ Name = $name.Name; Version = $name.Version.ToString(); PublicKeyToken = [Convert]::ToHexString($name.GetPublicKeyToken()) }
    } catch {
        # Native DLL, or non-CLI assembly. Do not load binaries to inspect them.
    }
    [pscustomobject]@{
        File = $_.Name
        Path = $relative
        Size = $_.Length
        Sha256 = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        Assembly = $assembly
    }
})

$collisions = @()
foreach ($group in ($dlls | Group-Object File)) {
    if ($group.Count -lt 2) { continue }
    $distinct = @($group.Group | Select-Object -ExpandProperty Sha256 -Unique)
    if ($distinct.Count -lt 2) { continue }
    $archOnly = $true
    foreach ($entry in $group.Group) {
        if ($entry.Path -notmatch '^(x64|x86|runtimes/(win-x86|win-x64)/native)/') { $archOnly = $false }
    }
    $classification = if ($archOnly) { 'architecture-specific' } else { 'needs-review' }
    $collisions += [pscustomobject]@{
        Name = $group.Name
        Classification = $classification
        Variants = @($group.Group | Sort-Object Path)
    }
    Write-Host ("DLL_COLLISION [{0}] {1}: {2}" -f $classification, $group.Name,
        (($group.Group | ForEach-Object { $_.Path + ':' + $_.Sha256.Substring(0, 12) }) -join '; '))
}

$report = [ordered]@{
    GeneratedUtc = [DateTime]::UtcNow.ToString('o')
    TotalDllFiles = $dlls.Count
    UniqueDllNames = @($dlls | Select-Object -ExpandProperty File -Unique).Count
    ConflictingNames = $collisions.Count
    ReviewRequired = @($collisions | Where-Object Classification -eq 'needs-review').Count
    Collisions = $collisions
}
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputPath -Encoding utf8
Write-Host ("NET10_DLL_AUDIT total={0} unique={1} conflicts={2} review_required={3}" -f
    $report.TotalDllFiles, $report.UniqueDllNames, $report.ConflictingNames, $report.ReviewRequired)
Write-Host "Audit report: $OutputPath"
# Important: do not fail on historical conflicts until each package and plugin has been retested.
