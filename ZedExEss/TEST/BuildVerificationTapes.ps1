param(
    [switch]$Raxoft,
    [switch]$ZxTests
)

$ErrorActionPreference = 'Stop'

if (-not $Raxoft -and -not $ZxTests) {
    $Raxoft = $true
    $ZxTests = $true
}

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Join-Bytes {
    param(
        [Parameter(Mandatory = $true)][byte[]]$First,
        [Parameter(Mandatory = $true)][byte[]]$Second
    )

    $joined = [byte[]]::new($First.Length + $Second.Length)
    [System.Buffer]::BlockCopy($First, 0, $joined, 0, $First.Length)
    [System.Buffer]::BlockCopy($Second, 0, $joined, $First.Length, $Second.Length)
    return $joined
}

function Resolve-RequiredTool {
    param([Parameter(Mandatory = $true)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required tool '$Name' was not found on PATH."
    }

    return $command.Source
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    foreach ($argument in $Arguments) {
        [void]$psi.ArgumentList.Add($argument)
    }

    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) {
        throw "$FileName $($Arguments -join ' ') failed with exit code $($process.ExitCode).`n$stdout`n$stderr"
    }
}

function Invoke-NativeToBytes {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][byte[]]$InputBytes
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    foreach ($argument in $Arguments) {
        [void]$psi.ArgumentList.Add($argument)
    }

    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    $process.StandardInput.BaseStream.Write($InputBytes, 0, $InputBytes.Length)
    $process.StandardInput.Close()

    $output = [System.IO.MemoryStream]::new()
    $process.StandardOutput.BaseStream.CopyTo($output)
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) {
        throw "$FileName $($Arguments -join ' ') failed with exit code $($process.ExitCode).`n$stderr"
    }

    return $output.ToArray()
}

function Build-RaxoftTapes {
    $sjasm = Resolve-RequiredTool 'sjasm'
    $mktap = Resolve-RequiredTool 'mktap'
    $dir = Join-Path $testRoot 'raxoft'
    $programs = @('z80full', 'z80flags', 'z80doc', 'z80docflags', 'z80ccf', 'z80memptr', 'z80ccfscr')
    $loaderBytes = [System.IO.File]::ReadAllBytes((Join-Path $dir 'loader.bas'))

    foreach ($program in $programs) {
        Invoke-Native -FileName $sjasm -Arguments @("$program.asm") -WorkingDirectory $dir

        $outPath = Join-Path $dir "$program.out"
        if (-not (Test-Path -LiteralPath $outPath)) {
            throw "Expected assembler output '$outPath' was not created."
        }

        $codeBytes = [System.IO.File]::ReadAllBytes($outPath)
        $basicTap = Invoke-NativeToBytes -FileName $mktap -Arguments @('-b', $program, '10') -WorkingDirectory $dir -InputBytes $loaderBytes
        $codeTap = Invoke-NativeToBytes -FileName $mktap -Arguments @($program, '32768') -WorkingDirectory $dir -InputBytes $codeBytes
        [System.IO.File]::WriteAllBytes((Join-Path $dir "$program.tap"), (Join-Bytes $basicTap $codeTap))
        Write-Host "Built TEST/raxoft/$program.tap"
    }
}

function Build-ZxTestTapes {
    $pasmo = Resolve-RequiredTool 'pasmo'
    $mktap = Resolve-RequiredTool 'mktap'
    $dir = Join-Path $testRoot 'zxtests-3'
    $programs = @('btime', 'stime', 'minfo', 'ulatest3')

    foreach ($program in $programs) {
        $tmpTap = Join-Path $dir 'tmp.tap'
        if (Test-Path -LiteralPath $tmpTap) {
            Remove-Item -LiteralPath $tmpTap -Force
        }

        Invoke-Native -FileName $pasmo -Arguments @('--alocal', '--tap', "$program.asm", 'tmp.tap') -WorkingDirectory $dir

        $basicBytes = [System.IO.File]::ReadAllBytes((Join-Path $dir "$program.bas"))
        $basicTap = Invoke-NativeToBytes -FileName $mktap -Arguments @('-b', $program, '9000') -WorkingDirectory $dir -InputBytes $basicBytes
        $codeTap = [System.IO.File]::ReadAllBytes($tmpTap)
        [System.IO.File]::WriteAllBytes((Join-Path $dir "$program.tap"), (Join-Bytes $basicTap $codeTap))
        Write-Host "Built TEST/zxtests-3/$program.tap"
    }
}

if ($Raxoft) {
    Build-RaxoftTapes
}

if ($ZxTests) {
    Build-ZxTestTapes
}
