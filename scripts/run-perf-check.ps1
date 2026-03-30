param(
    [int]$DurationSeconds = 600,
    [int]$Iterations = 2000,
    [string]$Scenarios = "1,10,50,100",
    [string]$OutputDir = "artifacts/perf",
    [switch]$Quick
)

$ErrorActionPreference = "Stop"

$argsList = @("--duration-seconds", $DurationSeconds, "--iterations", $Iterations, "--scenarios", $Scenarios, "--output", $OutputDir)
if ($Quick) {
    $argsList = @("--quick", "--output", $OutputDir)
}

Write-Host "Running SSHClient performance probe..."
Write-Host "DurationSeconds=$DurationSeconds, Iterations=$Iterations, Scenarios=$Scenarios, OutputDir=$OutputDir, Quick=$Quick"

dotnet run --project tools/SSHClient.PerfRunner/SSHClient.PerfRunner.csproj --configuration Release -- @argsList
