param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Pin source revisions: tests must not silently change when these repos sync upstream.
# Test resources remain in RUNNER_TEMP; do not check them into Switch-Toolbox or upload them as artifacts.
$sources = @(
    @{ Game = 'SV'; Repo = 'SV_Resources'; Commit = '4a39af505143fd2991a1edf7576c1f22546043fa'; Root = 'Unpack/4.0.0/romfs/arc/data.trpfs/virtual/pokemon/data' },
    @{ Game = 'ZA'; Repo = 'ZA_Resources'; Commit = '6637570583e96b64884943303d6eae8dc6884263'; Root = 'Unpack/v2.0.2/romfs/arc/data.trpfs/virtual/ik_pokemon/data' }
)

foreach ($src in $sources) {
    if ($src.Game -eq 'ZA' -and [string]::IsNullOrWhiteSpace($env:NET10_ZA_REPO_TOKEN)) {
        Write-Warning '[SKIP] Private ZA_Resources fixtures require NET10_ZA_REPO_TOKEN with read-only contents access.'
        continue
    }
    $target = Join-Path $Destination $src.Game
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($pokemon in @('pm0001', 'pm0025')) {
        $prefix = "$($src.Root)/$pokemon/${pokemon}_00_00"
        $icon = if ($src.Game -eq 'SV') { "${pokemon}_00_00_00.bntx" } else { "${pokemon}_00_00_00_0.bntx" }
        $fixtures = @(@{ Remote = "$prefix/icon/$icon"; Local = "$pokemon.bntx" })
        if ($pokemon -eq 'pm0001') {
            foreach ($ext in @('trmdl', 'trmsh', 'trmmt', 'trskl')) {
                $fixtures += @{ Remote = "$prefix/${pokemon}_00_00.$ext"; Local = "$pokemon.$ext" }
            }
            $fixtures += @{ Remote = "$prefix/${pokemon}_00_00_00000_defaultwait01_loop.tranm"; Local = "$pokemon.tranm" }
        }

        foreach ($fixture in $fixtures) {
            $url = "https://raw.githubusercontent.com/969981/$($src.Repo)/$($src.Commit)/$($fixture.Remote)"
            $output = Join-Path $target $fixture.Local
            $downloaded = $false
            for ($attempt = 1; $attempt -le 3; $attempt++) {
                try {
                    $headers = @{}
                    if ($src.Game -eq 'ZA') {
                        $headers['Authorization'] = 'Bearer ' + $env:NET10_ZA_REPO_TOKEN
                    }
                    Invoke-WebRequest -Uri $url -OutFile $output -Headers $headers -MaximumRedirection 5 -TimeoutSec 90
                    $downloaded = $true
                    break
                } catch {
                    if ($attempt -eq 3) { throw }
                    Start-Sleep -Seconds (3 * $attempt)
                }
            }
            if (-not $downloaded -or -not (Test-Path $output)) { throw "Fixture not downloaded: $url" }
            $item = Get-Item $output
            if ($item.Length -lt 16) { throw "Fixture too small: $url" }
            $hash = (Get-FileHash $output -Algorithm SHA256).Hash.ToLowerInvariant()
            Write-Host "REAL_FIXTURE $($src.Game) $($fixture.Local) size=$($item.Length) sha256=$hash"
        }
    }
}
Write-Host "Downloaded real game fixtures to $Destination (temporary runner files only)."
