# Runs every check that needs nothing but the .NET SDK and Python: the source checks, the proof
# that each of them catches what it claims to, and the test suites. GitHub runs this on every
# push and pull request; run it yourself before sending a change.
#
#     powershell -ExecutionPolicy Bypass -File tests\run.ps1 [-Notes <release-notes.md>]
#
# -Notes checks a release's notes before they are published. Without it, the notes of the
# latest published release are checked, when the GitHub CLI is available to fetch them.
#
# Exits with 0 only when everything ran and nothing failed. A suite that cannot be built, or
# that prints no result, counts as a failure: a check that silently did not run is the kind of
# gap this exists to stop.
param([string]$Notes = "")

$ErrorActionPreference = 'Stop'
$tests = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo  = Split-Path -Parent $tests

# The bundled third-party programs first: Pulse's build copies PresentMon into its output, and
# the frame capture suite runs it. Fetched and verified, never committed. A fresh clone has
# neither, which is how this ordering was found.
& (Join-Path $repo 'fetch-third-party.ps1') | Out-Null

# Every suite reads the code under test from Pulse.dll in the Debug output, so it is built first;
# otherwise the suites would quietly test whatever was built last.
Write-Output "Building Pulse..."
& dotnet build (Join-Path $repo 'Pulse.csproj') -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Pulse did not build' }
$pulseOut = Join-Path $repo 'bin\Debug\net10.0-windows'

$results = New-Object System.Collections.Generic.List[object]

function Tally([string]$text) {
    if ($text -match '(\d+) passed, (\d+) failed')            { return @([int]$Matches[1], [int]$Matches[2]) }
    if ($text -match '(\d+) guards have teeth, (\d+) do not') { return @([int]$Matches[1], [int]$Matches[2]) }
    if ($text -match 'All tests pass now and all of them fail against the pre-fix code\.') {
        $n = ([regex]::Matches($text, 'PASS    FAIL')).Count + ([regex]::Matches($text, 'PASS    PASS')).Count
        return @($n, 0)
    }
    return $null
}

function Record($name, $text) {
    $t = Tally $text
    if ($t) { $results.Add([pscustomobject]@{ Name = $name; Passed = $t[0]; Failed = $t[1]; Note = '' }) }
    else    { $results.Add([pscustomobject]@{ Name = $name; Passed = 0; Failed = 1; Note = 'printed no result' }) }
    if (-not $t -or $t[1] -gt 0) { Write-Output $text }
    # On GitHub every report is kept, folded, so a passing run can be read check by check too.
    elseif ($env:GITHUB_ACTIONS) { Write-Output "::group::$name report"; Write-Output $text; Write-Output '::endgroup::' }
}

$python = @('python', 'py') | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $python) { throw 'Python 3 is needed for the source checks.' }

# From here every check and suite is judged by the result it prints, not by whether it wrote to
# its error output. Under 'Stop', Windows PowerShell turns any line a program writes there into
# an error that ends the whole run, so one harmless warning hid every result after it. That is
# how the first run on GitHub ended, on a warning from a newer Python than ours.
$ErrorActionPreference = 'Continue'

foreach ($check in @(@('hygiene', 'checks\hygiene.py', ''),
                     @('hygiene catches', 'checks\hygiene.py', '--self-test'),
                     @('regression guards', 'checks\guards.py', ''),
                     @('guards have teeth', 'checks\teeth.py', ''))) {
    Write-Output "Running $($check[0])..."
    $extra = @(); if ($check[2]) { $extra += $check[2] }
    $text = (& $python (Join-Path $tests $check[1]) @extra 2>&1 | Out-String)
    Record $check[0] $text
}

# Name, folder, executable, and whether the suite loads Pulse.dll.
$suites = @(
    @('logging',           'logs',              'LogTest.exe',          $true),
    @('overlay position',  'overlay-position',  'PosTest.exe',          $false),
    @('wire format',       'wire-format',       'ProtoTest.exe',        $true),
    @('memory capacity',   'capacity',          'CapacityTest.exe',     $true),
    @('update download',   'update-download',   'DownloadTest.exe',     $true),
    @('settings save',     'settings-save',     'SaveTest.exe',         $true),
    @('frame capture',     'frame-capture',     'CaptureTest.exe',      $true),
    @('resilience',        'resilience',        'ResilienceTest.exe',   $true),
    @('shortcuts',         'shortcuts',         'ShortcutTest.exe',     $true),
    @('tile availability', 'tile-availability', 'AvailabilityTest.exe', $false),
    @('stability',         'stability',         'StabilityTest.exe',    $true),
    @('release notes',     'release-notes',     'NotesTest.exe',        $true)
)

# The release notes suite checks a notes file: the one given, or the latest published.
$notesFile = $Notes
if (-not $notesFile -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    $published = & gh release view --repo refora-technologies/pulse-monitor --json body -q .body 2>$null
    if ($LASTEXITCODE -eq 0 -and $published) {
        $notesFile = Join-Path ([IO.Path]::GetTempPath()) 'pulse-published-notes.md'
        Set-Content -Path $notesFile -Value $published -Encoding UTF8
    }
}

foreach ($s in $suites) {
    $name, $folder, $exe, $needsPulse = $s
    Write-Output "Running $name..."
    $dir = Join-Path $tests "suites\$folder"
    $build = & dotnet build $dir -c Release --nologo -v q 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        $results.Add([pscustomobject]@{ Name = $name; Passed = 0; Failed = 1; Note = 'did not build' })
        Write-Output $build
        continue
    }
    $out = Join-Path $dir 'bin\Release\net10.0-windows'
    if ($needsPulse) { Copy-Item (Join-Path $pulseOut '*.dll') $out -Force }

    $arguments = @()
    if ($folder -eq 'release-notes' -and $notesFile) { $arguments += $notesFile }

    Push-Location $dir
    try { $text = (& (Join-Path $out $exe) @arguments 2>&1 | Out-String) }
    finally { Pop-Location }

    # The overlay position suite is a windowed app and writes its report to a file instead.
    $report = Join-Path $out 'position-tests.txt'
    if (Test-Path $report) { $text = Get-Content $report -Raw; Remove-Item $report }

    Record $name $text
}

$passed = ($results | Measure-Object Passed -Sum).Sum
$failed = ($results | Measure-Object Failed -Sum).Sum
Write-Output ''
Write-Output ('=' * 62)
foreach ($r in $results) {
    Write-Output ('  {0,-20} {1,5} passed {2,4} failed  {3}' -f $r.Name, $r.Passed, $r.Failed, $r.Note)
}
Write-Output ('=' * 62)
Write-Output ('  {0,-20} {1,5} passed {2,4} failed' -f 'TOTAL', $passed, $failed)
if ($failed -gt 0) { exit 1 }
exit 0
