param(
    [string]$ProgramPath,
    [string]$OutputPath,
    [long]$MaxInstructions = 5000000000,
    [switch]$Rebuild
)

# Keep assembly compatibility and execution behaviour in one place. This wrapper
# exists so the MEMPTR validation has an obvious, discoverable command of its own.
$arguments = @{
    TestName = 'z80memptr'
    MaxInstructions = $MaxInstructions
}

if (-not [string]::IsNullOrWhiteSpace($ProgramPath)) {
    $arguments.ProgramPath = $ProgramPath
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $arguments.OutputPath = $OutputPath
}

if ($Rebuild) {
    $arguments.Rebuild = $true
}

& (Join-Path $PSScriptRoot 'RunRaxoftZ80Full.ps1') @arguments
exit $LASTEXITCODE
