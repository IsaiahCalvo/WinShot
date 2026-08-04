<#
.SYNOPSIS
  Runs the WinShot test suite on a hidden Windows desktop so test windows (render
  harnesses, capture overlays) never flash on the user's screen or taskbar.
  Always use this instead of a bare `dotnet test` on a machine someone is using —
  the VisibleDesktopGuard test fails a bare run on purpose.

.PARAMETER Filter
  Optional xunit --filter expression, e.g. "FullyQualifiedName~ScrollMatcher".
#>
[CmdletBinding()]
param([string]$Filter)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$testProj = Join-Path $repo 'tests\WinShot.Tests\WinShot.Tests.csproj'
$runnerProj = Join-Path $PSScriptRoot 'hidden-test-runner\HiddenTestRunner.csproj'
$runner = Join-Path $PSScriptRoot 'hidden-test-runner\bin\Release\net8.0\HiddenDesktopRunner.exe'

# Build everything on the visible desktop (builds show no windows); only the test
# run itself moves to the hidden desktop.
dotnet build $runnerProj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build $testProj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$testArgs = @((Get-Command dotnet).Source, 'test', $testProj, '-c', 'Release', '--nologo', '--no-build')
if ($Filter) { $testArgs += @('--filter', $Filter) }
& $runner @testArgs
exit $LASTEXITCODE
