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

# The DockPanelSuite implementation and VS2015 theme must be the exact managed
# binaries restored by NuGet in the root output, not historical DLLs under Lib/.
# Audit both compile/publish layout and the old plugin probing directory.
$dockPanelBindings = @()
$dockPanelFailures = @()
foreach ($name in @('WeifenLuo.WinFormsUI.Docking.dll',
                     'WeifenLuo.WinFormsUI.Docking.ThemeVS2015.dll')) {
    $expected = @($dlls | Where-Object { $_.Path -eq $name })
    $legacyPath = 'Lib/' + $name
    $legacy = @($dlls | Where-Object { $_.Path -eq $legacyPath })
    if ($expected.Count -ne 1 -or $legacy.Count -ne 1) {
        $dockPanelFailures += "Expected one published root and one Lib copy of $name"
        continue
    }
    $rootDll = $expected[0]
    $libDll = $legacy[0]
    if ($null -eq $rootDll.Assembly -or $null -eq $libDll.Assembly) {
        $dockPanelFailures += "$name is not a managed assembly"
        continue
    }
    if ($rootDll.Sha256 -ne $libDll.Sha256) {
        $dockPanelFailures += "Root/Lib assembly hashes differ: $name"
    }
    if ($rootDll.Assembly.Name -ne $libDll.Assembly.Name -or
        $rootDll.Assembly.Version -ne $libDll.Assembly.Version) {
        $dockPanelFailures += "Root/Lib assembly identities differ: $name"
    }
    # Also catch third-party plugin folders shadowing an assembly by filename.
    $shadowed = @($dlls | Where-Object {
        $_.File -eq $name -and $_.Sha256 -ne $rootDll.Sha256
    })
    if ($shadowed.Count -gt 0) {
        $dockPanelFailures += "Different $name DLL in: $(($shadowed.Path) -join ', ')"
    }
    $dockPanelBindings += [pscustomobject]@{
        Name = $name
        ManagedAssembly = $rootDll.Assembly.Name
        Version = $rootDll.Assembly.Version
        Sha256 = $rootDll.Sha256
        RootPath = $rootDll.Path
        LibPath = $libDll.Path
    }
}
$dockPanelPass = $dockPanelFailures.Count -eq 0

$report = [ordered]@{
    GeneratedUtc = [DateTime]::UtcNow.ToString('o')
    TotalDllFiles = $dlls.Count
    UniqueDllNames = @($dlls | Select-Object -ExpandProperty File -Unique).Count
    ConflictingNames = $collisions.Count
    ReviewRequired = @($collisions | Where-Object Classification -eq 'needs-review').Count
    Collisions = $collisions
    DockPanelSuite = [ordered]@{
        Passed = $dockPanelPass
        Bindings = $dockPanelBindings
        Failures = $dockPanelFailures
    }
}
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputPath -Encoding utf8
Write-Host ("NET10_DLL_AUDIT total={0} unique={1} conflicts={2} review_required={3}" -f
    $report.TotalDllFiles, $report.UniqueDllNames, $report.ConflictingNames, $report.ReviewRequired)
Write-Host "Audit report: $OutputPath"
# Important: do not fail on historical conflicts until each package and plugin has been retested.

if (-not $dockPanelPass) {
    throw "DockPanelSuite binding gate failed: $(($dockPanelFailures) -join '; ')"
}
Write-Host 'NET10_DOCKPANEL_BINDING PASS: root and Lib assemblies are identical.'
