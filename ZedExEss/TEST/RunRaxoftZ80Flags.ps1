param(
    [string]$ProgramPath,
    [string]$OutputPath,
    [long]$MaxInstructions = 5000000000,
    [switch]$Rebuild
)

$runner = Join-Path $PSScriptRoot 'RunRaxoftZ80Full.ps1'
& $runner -TestName z80flags -ProgramPath $ProgramPath -OutputPath $OutputPath -MaxInstructions $MaxInstructions -Rebuild:$Rebuild
exit $LASTEXITCODE
