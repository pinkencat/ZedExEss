param(
    [ValidateSet('z80full', 'z80memptr', 'z80flags', 'z80ccf')]
    [string]$TestName = 'z80full',
    [string]$ProgramPath,
    [string]$OutputPath,
    [long]$MaxInstructions = 5000000000,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testRoot
$raxoftRoot = Join-Path $testRoot 'raxoft'
$projectPath = Join-Path $repoRoot 'ZedExEss.csproj'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $raxoftRoot "$TestName-results.txt"
}

function Test-NonEmptyFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-Item -LiteralPath $Path).Length -gt 0)
}

function Resolve-Assembler {
    foreach ($name in @('sjasmplus', 'sjasm')) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command
        }
    }

    return $null
}

function Invoke-NativeToLog {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    $psi.Arguments = Join-ProcessArguments $Arguments
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    # Drain both redirected streams concurrently. Reading one stream to EOF before
    # touching the other can deadlock when an assembler emits enough diagnostics to
    # fill the second pipe (the post-CCF source is large enough to expose this).
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($stdoutTask, $stderrTask))
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result

    [System.IO.File]::WriteAllText($LogPath, "$stdout$stderr")
    return $process.ExitCode
}

function Join-ProcessArguments {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $quoted = foreach ($argument in $Arguments) {
        if ($argument -match '[\s"]') {
            $escaped = $argument.Replace('\', '\\').Replace('"', '\"')
            '"' + $escaped + '"'
        }
        else {
            $argument
        }
    }

    return ($quoted -join ' ')
}

function New-CombineMacroText {
    $builder = [System.Text.StringBuilder]::new()

    [void]$builder.AppendLine('            macro   combineOpcode base,offset')
    foreach ($index in 0..2) {
        if ($index -eq 2) {
            [void]$builder.AppendLine('            if      postccf')
        }
        [void]$builder.AppendLine('            ld      a,(bc)')
        [void]$builder.AppendLine('            xor     (hl)')
        [void]$builder.AppendLine('            ex      de,hl')
        [void]$builder.AppendLine('            xor     (hl)')
        if ($index -eq 0) {
            [void]$builder.AppendLine('            ld      (base+offset),a')
        }
        else {
            [void]$builder.AppendLine("            ld      (base+offset+$index),a")
        }

        [void]$builder.AppendLine('            inc     c')
        [void]$builder.AppendLine('            inc     e')
        [void]$builder.AppendLine('            inc     l')
        if ($index -eq 2) {
            [void]$builder.AppendLine('            endif')
        }
    }
    [void]$builder.AppendLine('            endm')
    [void]$builder.AppendLine()

    [void]$builder.AppendLine('            macro   combine16 base')
    foreach ($index in 0..15) {
        [void]$builder.AppendLine('            ld      a,(bc)')
        [void]$builder.AppendLine('            xor     (hl)')
        [void]$builder.AppendLine('            ex      de,hl')
        [void]$builder.AppendLine('            xor     (hl)')
        if ($index -eq 0) {
            [void]$builder.AppendLine('            ld      (base),a')
        }
        else {
            [void]$builder.AppendLine("            ld      (base+$index),a")
        }

        if ($index -lt 15) {
            [void]$builder.AppendLine('            inc     c')
            [void]$builder.AppendLine('            inc     e')
            [void]$builder.AppendLine('            inc     l')
        }
    }
    [void]$builder.AppendLine('            endm')

    return $builder.ToString()
}

function New-VecMacroText {
    # SjASMPlus performs textual macro-parameter substitution differently from
    # the SjASM version used to build the published Raxoft TAP files.  The
    # original formal names (mem, a, bc, de, hl, ix, iy and sp) collide with
    # symbols passed as arguments.  For example, "hl,mem" was incorrectly
    # assembled as "hl,0x1200" because the formal parameter named mem shadowed
    # the global scratch-memory symbol.  Prefix every formal name so arguments
    # continue to resolve in the caller's symbol scope.
    return @'
            macro   vec _op1,_op2,_op3,_op4,_memn,_mem,_an,_a,_fn,_f,_bcn,_bc,_den,_de,_hln,_hl,_ixn,_ix,_iyn,_iy,_spn,_sp

            if      postccf

            if      ( veccount % 3 ) == 0
            inst    _op1,_op2,_op3,_op4,tail
areg       =       0
            else
            db      _op1,_op2,_op3,_op4,0
areg       =       areg | _a
            endif

            else
            db      _op1,_op2,_op3,_op4
            endif

            db      _f

            if      postccf & ( ( veccount % 3 ) == 2 )
            db      _a | ( ( ~ areg ) & 0x28 )
            else
            db      _a
            endif

            dw      _bc,_de,_hl,_ix,_iy
            dw      _mem
            dw      _sp

veccount   =       veccount+1

            endm
'@
}

function New-InstMacroText {
    # The original SjASM source uses IFIDN to find the symbolic `stop`
    # sentinel. SjASMPlus does not implement IFIDN, but the sentinel is an
    # ordinary numeric constant there, so expression comparisons are exact.
    return @'
            macro   inst _op1,_op2,_op3,_op4,_tail
            if      _op4 = stop
            db      _op1,_op2,_op3,_tail,0
            else
            if      _op3 = stop
            db      _op1,_op2,_tail,_op4,0
            else
            if      _op2 = stop
            db      _op1,_tail,_op3,_op4,0
            else
            db      _op1,_op2,_op3,_op4,_tail
            endif
            endif
            endif
            endm
'@
}

function New-SjasmPlusCompatSourceTree {
    $compatRoot = Join-Path $repoRoot 'obj\raxoft-sjasmplus'
    if (Test-Path -LiteralPath $compatRoot) {
        Remove-Item -LiteralPath $compatRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $compatRoot | Out-Null
    Copy-Item -Path (Join-Path $raxoftRoot '*') -Destination $compatRoot -Recurse -Force

    $ideaPath = Join-Path $compatRoot 'idea.asm'
    $idea = [System.IO.File]::ReadAllText($ideaPath)
    $combineMacroPattern = '(?s)\s+macro\s+combine base,count,offset:0,last:1.*?^\s+endm'
    $combineMacroRegex = [regex]::new($combineMacroPattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    $idea = $combineMacroRegex.Replace($idea, "`r`n$(New-CombineMacroText)", 1)
    $idea = $idea.Replace('            combine .opcode,opsize-2,2,0', '            combineOpcode .opcode,2')
    $idea = $idea.Replace('            combine data,datasize', '            combine16 data')
    [System.IO.File]::WriteAllText($ideaPath, $idea, [System.Text.Encoding]::ASCII)

    $testMacrosPath = Join-Path $compatRoot 'testmacros.asm'
    $testMacros = [System.IO.File]::ReadAllText($testMacrosPath)
    $instMacroPattern = '(?ms)^\s*macro\s+inst\b.*?^\s*endm\s*$'
    $instMacroRegex = [regex]::new($instMacroPattern)
    $testMacros = $instMacroRegex.Replace($testMacros, (New-InstMacroText), 1)
    $vecMacroPattern = '(?ms)^\s*macro\s+vec\b.*?^\s*endm\s*$'
    $vecMacroRegex = [regex]::new($vecMacroPattern)
    $testMacros = $vecMacroRegex.Replace($testMacros, (New-VecMacroText), 1)
    $testMacros = $testMacros.Replace('.veccount := 0', 'veccount = 0')
    $testMacros = $testMacros.Replace('.@veccount', 'veccount')
    $testMacros = $testMacros.Replace('.@areg', 'areg')
    $testMacros = $testMacros.Replace(':=', '=')
    [System.IO.File]::WriteAllText($testMacrosPath, $testMacros, [System.Text.Encoding]::ASCII)

    $testsPath = Join-Path $compatRoot 'tests.asm'
    $tests = [System.IO.File]::ReadAllText($testsPath)
    $tests = [regex]::Replace($tests, '(?m)^(\s*)\.(\w+)', '$1testtable.$2')
    [System.IO.File]::WriteAllText($testsPath, $tests, [System.Text.Encoding]::ASCII)

    return $compatRoot
}

if ([string]::IsNullOrWhiteSpace($ProgramPath)) {
    $ProgramPath = Join-Path $raxoftRoot "$TestName.out"

    if ($Rebuild -or -not (Test-NonEmptyFile $ProgramPath)) {
        $assembler = Resolve-Assembler
        if ($null -eq $assembler) {
            throw "$TestName.out is missing or empty and neither 'sjasmplus' nor 'sjasm' is on PATH. Install sjasmplus, or pass -ProgramPath pointing at an assembled $TestName.out or $TestName.tap."
        }

        if ((Test-Path -LiteralPath $ProgramPath -PathType Leaf) -and ((Get-Item -LiteralPath $ProgramPath).Length -eq 0)) {
            Remove-Item -LiteralPath $ProgramPath -Force
        }

        $assembleLog = Join-Path $raxoftRoot "$TestName-assemble.log"
        $listPath = Join-Path $raxoftRoot "$TestName.lst"
        $assembleRoot = $raxoftRoot
        $assemblerArgs = @("$TestName.asm")

        if ($assembler.Name -ieq 'sjasmplus.exe' -or $assembler.Name -ieq 'sjasmplus') {
            $assembleRoot = New-SjasmPlusCompatSourceTree
            $assemblerArgs = @("--raw=$TestName.out", "--lst=$TestName.lst", "$TestName.asm")
        }

        if (Test-Path -LiteralPath $ProgramPath -PathType Leaf) {
            Remove-Item -LiteralPath $ProgramPath -Force
        }
        if (Test-Path -LiteralPath $listPath -PathType Leaf) {
            Remove-Item -LiteralPath $listPath -Force
        }

        $assemblerExitCode = Invoke-NativeToLog -FileName $assembler.Source -Arguments $assemblerArgs -WorkingDirectory $assembleRoot -LogPath $assembleLog
        if ($assemblerExitCode -ne 0) {
            if (Test-Path -LiteralPath $ProgramPath -PathType Leaf) {
                Remove-Item -LiteralPath $ProgramPath -Force
            }
            if (Test-Path -LiteralPath $listPath -PathType Leaf) {
                Remove-Item -LiteralPath $listPath -Force
            }

            Write-Host "Assembler failed. First log lines from ${assembleLog}:"
            Get-Content -LiteralPath $assembleLog -TotalCount 60
            throw "$($assembler.Name) failed with exit code $assemblerExitCode. Use the assembler/toolchain you normally use to build the Raxoft tapes, or pass -ProgramPath pointing at an already assembled $TestName.out or $TestName.tap."
        }

        $assembledOut = Join-Path $assembleRoot "$TestName.out"
        if (Test-NonEmptyFile $assembledOut) {
            Copy-Item -LiteralPath $assembledOut -Destination $ProgramPath -Force
        }

        $assembledList = Join-Path $assembleRoot "$TestName.lst"
        if (Test-NonEmptyFile $assembledList) {
            Copy-Item -LiteralPath $assembledList -Destination $listPath -Force
        }

        if (-not (Test-NonEmptyFile $ProgramPath)) {
            throw "sjasm completed but did not produce a non-empty '$ProgramPath'."
        }
    }
}

if (-not (Test-NonEmptyFile $ProgramPath)) {
    throw "Program file '$ProgramPath' is missing or empty."
}

if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
}

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$runnerSwitch = switch ($TestName) {
    'z80memptr' { '--raxoft-z80memptr' }
    'z80flags'  { '--raxoft-z80flags' }
    'z80ccf'    { '--raxoft-z80ccf' }
    default     { '--raxoft-z80full' }
}
dotnet run --project $projectPath -c Release -- $runnerSwitch --raxoft-program $ProgramPath --raxoft-output $OutputPath --raxoft-max-instructions $MaxInstructions
$exitCode = $LASTEXITCODE

if (Test-Path -LiteralPath $OutputPath -PathType Leaf) {
    Write-Host ""
    Write-Host "==== $OutputPath ===="
    Get-Content -LiteralPath $OutputPath
}

exit $exitCode
