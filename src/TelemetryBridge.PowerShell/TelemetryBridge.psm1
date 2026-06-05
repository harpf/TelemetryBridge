#Requires -Version 5.1
Set-StrictMode -Version Latest

<#
    TelemetryBridge PowerShell module

    A thin, ergonomic wrapper over the built TelemetryBridge CLI. The module shells
    out to the CLI's `send job` / `buffer status` verbs; it contains no telemetry
    logic of its own. The single small seam used for testing is the private
    `Invoke-TelemetryBridgeCli` function — every CLI call funnels through it, so
    Pester can mock it and assert on the exact argument vector.
#>

# ------------------------------------------------------------------------------
# CLI resolution
# ------------------------------------------------------------------------------

# Build the (FilePath, Prefix) pair used to launch a resolved CLI artifact. A
# managed .dll is launched via `dotnet <dll>`; a native executable is launched
# directly.
function New-TelemetryBridgeInvocation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if ([System.IO.Path]::GetExtension($Path) -ieq '.dll') {
        return [pscustomobject]@{ FilePath = 'dotnet'; Prefix = @($Path); Source = $Path }
    }

    return [pscustomobject]@{ FilePath = $Path; Prefix = @(); Source = $Path }
}

# Look for a locally built CLI next to the module (src/TelemetryBridge.Cli/bin/**).
# Prefers a native .exe over the .dll, and the most recently built artifact.
function Find-TelemetryBridgeCli {
    [CmdletBinding()]
    param()

    # Module lives at <repo>/src/TelemetryBridge.PowerShell, so the repo root is
    # two directories up.
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $cliBin = Join-Path $repoRoot 'src/TelemetryBridge.Cli/bin'
    if (-not (Test-Path -LiteralPath $cliBin)) {
        return $null
    }

    $candidate = Get-ChildItem -LiteralPath $cliBin -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('TelemetryBridge.Cli.exe', 'TelemetryBridge.Cli.dll') } |
        Sort-Object @{ Expression = { $_.Extension -ieq '.exe' }; Descending = $true },
                    @{ Expression = { $_.LastWriteTimeUtc };       Descending = $true } |
        Select-Object -First 1

    if ($candidate) { return $candidate.FullName }
    return $null
}

# Resolve the CLI to invoke, in priority order:
#   1. explicit -CliPath
#   2. TELEMETRYBRIDGE_CLI environment variable
#   3. 'telemetrybridge' (or .exe) on PATH
#   4. a locally built artifact under src/TelemetryBridge.Cli/bin
# Throws a clear, actionable message when nothing is found.
function Resolve-TelemetryBridgeCli {
    [CmdletBinding()]
    param([string]$CliPath)

    $candidate = $CliPath
    $origin = '-CliPath'
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = $env:TELEMETRYBRIDGE_CLI
        $origin = 'TELEMETRYBRIDGE_CLI environment variable'
    }

    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            throw "TelemetryBridge CLI not found at '$candidate' (from $origin)."
        }
        return New-TelemetryBridgeInvocation -Path (Resolve-Path -LiteralPath $candidate).Path
    }

    $onPath = Get-Command -Name 'telemetrybridge', 'telemetrybridge.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($onPath) {
        return New-TelemetryBridgeInvocation -Path $onPath.Source
    }

    $built = Find-TelemetryBridgeCli
    if ($built) {
        return New-TelemetryBridgeInvocation -Path $built
    }

    throw @"
Could not locate the TelemetryBridge CLI. Try one of:
  - pass -CliPath '<path-to-telemetrybridge[.exe|.dll]>'
  - set the TELEMETRYBRIDGE_CLI environment variable
  - put 'telemetrybridge' on your PATH
  - build the CLI: dotnet build src/TelemetryBridge.Cli
"@
}

# ------------------------------------------------------------------------------
# CLI invocation (the single test seam — mock this in Pester)
# ------------------------------------------------------------------------------

# Runs the resolved CLI with the supplied argument vector and returns the exit
# code plus combined output. Kept tiny and side-effect-only so it is trivially
# mockable; all command building lives in the public cmdlets.
function Invoke-TelemetryBridgeCli {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$CliPath
    )

    $cli = Resolve-TelemetryBridgeCli -CliPath $CliPath
    $allArgs = @($cli.Prefix) + $Arguments

    Write-Verbose "Invoking: $($cli.FilePath) $($allArgs -join ' ')"

    $output = & $cli.FilePath @allArgs 2>&1 | Out-String
    $exitCode = $LASTEXITCODE

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = $output
        Command  = $cli.Source
    }
}

# ------------------------------------------------------------------------------
# Public cmdlets
# ------------------------------------------------------------------------------

<#
.SYNOPSIS
    Sends a job event to the TelemetryBridge CLI ('send job' passthrough).

.DESCRIPTION
    Thin, typed wrapper over `telemetrybridge send job`. Maps parameters onto CLI
    flags and maps the -Attributes hashtable onto repeated --attr key=value args.

    In non-strict mode the CLI returns exit code 0 even when the export fails (the
    event stays durably buffered), so this cmdlet only throws on a genuine failure
    (argument/config error, or an export failure when -Strict is set).

.EXAMPLE
    Send-TelemetryJob -JobName 'Sync-Users' -Status succeeded -DurationMs 1234

.EXAMPLE
    Send-TelemetryJob -JobName 'Sync-Users' -Status failed -ErrorMessage 'timed out' -Strict
#>
function Send-TelemetryJob {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string]$JobName,

        [ValidateSet('started', 'succeeded', 'failed', 'warning', 'skipped',
            'cancelled', 'timeout', 'interrupted', 'unknown')]
        [string]$Status = 'succeeded',

        [string]$System = 'powershell',

        [ValidateSet('job', 'job-start', 'job-end')]
        [string]$EventType,

        [long]$DurationMs,
        [int]$ExitCode,
        [string]$CorrelationId,
        [string]$ParentCorrelationId,
        [string]$Instance,
        [string]$ScriptPath,
        [string]$ScriptName,
        [string]$ScriptVersion,
        [string]$ErrorMessage,
        [string]$ErrorType,
        [string]$ErrorCode,
        [hashtable]$Attributes,
        [switch]$Strict,
        [string]$CliPath
    )

    $cliArgs = [System.Collections.Generic.List[string]]::new()

    # --strict is a global flag, conventionally placed before the verb.
    if ($Strict) { $cliArgs.Add('--strict') }

    $cliArgs.Add('send')
    $cliArgs.Add('job')
    $cliArgs.Add('--job-name'); $cliArgs.Add($JobName)
    $cliArgs.Add('--status');   $cliArgs.Add($Status)
    $cliArgs.Add('--system');   $cliArgs.Add($System)

    # Only forward optional scalars the caller actually supplied. ContainsKey is
    # used (rather than truthiness) so explicit 0 / empty values are honored.
    if ($PSBoundParameters.ContainsKey('EventType'))           { $cliArgs.Add('--event-type');            $cliArgs.Add($EventType) }
    if ($PSBoundParameters.ContainsKey('DurationMs'))          { $cliArgs.Add('--duration-ms');           $cliArgs.Add($DurationMs.ToString([System.Globalization.CultureInfo]::InvariantCulture)) }
    if ($PSBoundParameters.ContainsKey('ExitCode'))            { $cliArgs.Add('--exit-code');             $cliArgs.Add($ExitCode.ToString([System.Globalization.CultureInfo]::InvariantCulture)) }
    if ($PSBoundParameters.ContainsKey('CorrelationId'))       { $cliArgs.Add('--correlation-id');        $cliArgs.Add($CorrelationId) }
    if ($PSBoundParameters.ContainsKey('ParentCorrelationId')) { $cliArgs.Add('--parent-correlation-id'); $cliArgs.Add($ParentCorrelationId) }
    if ($PSBoundParameters.ContainsKey('Instance'))            { $cliArgs.Add('--instance');              $cliArgs.Add($Instance) }
    if ($PSBoundParameters.ContainsKey('ScriptPath'))          { $cliArgs.Add('--script-path');           $cliArgs.Add($ScriptPath) }
    if ($PSBoundParameters.ContainsKey('ScriptName'))          { $cliArgs.Add('--script-name');           $cliArgs.Add($ScriptName) }
    if ($PSBoundParameters.ContainsKey('ScriptVersion'))       { $cliArgs.Add('--script-version');        $cliArgs.Add($ScriptVersion) }
    if ($PSBoundParameters.ContainsKey('ErrorMessage'))        { $cliArgs.Add('--error-message');         $cliArgs.Add($ErrorMessage) }
    if ($PSBoundParameters.ContainsKey('ErrorType'))           { $cliArgs.Add('--error-type');            $cliArgs.Add($ErrorType) }
    if ($PSBoundParameters.ContainsKey('ErrorCode'))           { $cliArgs.Add('--error-code');            $cliArgs.Add($ErrorCode) }

    if ($Attributes) {
        foreach ($key in $Attributes.Keys) {
            $value = $Attributes[$key]
            $cliArgs.Add('--attr')
            $cliArgs.Add("$key=$value")
        }
    }

    $result = Invoke-TelemetryBridgeCli -Arguments $cliArgs.ToArray() -CliPath $CliPath

    if ($result.ExitCode -ne 0) {
        throw "telemetrybridge 'send job' exited with code $($result.ExitCode) for job '$JobName'.`n$($result.Output)"
    }

    return [pscustomobject]@{
        JobName  = $JobName
        Status   = $Status
        ExitCode = $result.ExitCode
        Output   = $result.Output.TrimEnd()
    }
}

<#
.SYNOPSIS
    Runs a script block and reports its outcome to TelemetryBridge.

.DESCRIPTION
    Wraps a script block, capturing its duration and success/failure, then sends a
    job event (status 'succeeded' or 'failed' with the error message) via the CLI.

    Telemetry is best-effort: the wrapped block's original output and its
    success/failure are always preserved. A failing block is re-thrown unchanged
    after telemetry is recorded, and a telemetry failure never changes the job
    outcome — unless -Strict is set, in which case a telemetry export failure is
    surfaced (only when the wrapped block itself succeeded).

.EXAMPLE
    Invoke-WithTelemetry -JobName 'Sync-Users' -System 'powershell' -ScriptBlock {
        .\Sync-Users.ps1
    }

.EXAMPLE
    Invoke-WithTelemetry -JobName 'Nightly-ETL' -Attributes @{ env = 'prod'; region = 'eu' } -Strict -ScriptBlock {
        Invoke-Etl
    }
#>
function Invoke-WithTelemetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$JobName,
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [string]$System = 'powershell',
        [switch]$Strict,
        [hashtable]$Attributes,
        [string]$CorrelationId,
        [string]$ParentCorrelationId,
        [string]$Instance = [Environment]::MachineName,
        [string]$ScriptName,
        [string]$ScriptPath,
        [string]$EventType,
        [string]$CliPath
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'succeeded'
    $errorMessage = $null
    $errorType = $null
    $threw = $false
    $caught = $null
    $output = $null

    try {
        $output = & $ScriptBlock
    }
    catch {
        $threw = $true
        $caught = $_
        $status = 'failed'
        $errorMessage = $_.Exception.Message
        $errorType = $_.Exception.GetType().FullName
    }
    finally {
        $stopwatch.Stop()
    }

    $sendParams = @{
        JobName    = $JobName
        Status     = $status
        System     = $System
        DurationMs = [long][math]::Round($stopwatch.Elapsed.TotalMilliseconds)
    }
    if ($PSBoundParameters.ContainsKey('EventType'))           { $sendParams.EventType = $EventType }
    if ($Attributes)                                           { $sendParams.Attributes = $Attributes }
    if ($PSBoundParameters.ContainsKey('CorrelationId'))       { $sendParams.CorrelationId = $CorrelationId }
    if ($PSBoundParameters.ContainsKey('ParentCorrelationId')) { $sendParams.ParentCorrelationId = $ParentCorrelationId }
    if (-not [string]::IsNullOrWhiteSpace($Instance))          { $sendParams.Instance = $Instance }
    if ($PSBoundParameters.ContainsKey('ScriptName'))          { $sendParams.ScriptName = $ScriptName }
    if ($PSBoundParameters.ContainsKey('ScriptPath'))          { $sendParams.ScriptPath = $ScriptPath }
    if ($PSBoundParameters.ContainsKey('CliPath'))             { $sendParams.CliPath = $CliPath }
    if ($Strict)                                               { $sendParams.Strict = $true }
    if ($errorMessage)                                         { $sendParams.ErrorMessage = $errorMessage }
    if ($errorType)                                            { $sendParams.ErrorType = $errorType }

    $telemetryError = $null
    try {
        Send-TelemetryJob @sendParams | Out-Null
    }
    catch {
        $telemetryError = $_
        Write-Warning "TelemetryBridge: failed to record telemetry for job '$JobName': $($_.Exception.Message)"
    }

    # The wrapped block's outcome always wins: re-throw its failure unchanged.
    if ($threw) {
        throw $caught
    }

    # The block succeeded. In strict mode a telemetry export failure is surfaced.
    if ($Strict -and $telemetryError) {
        throw $telemetryError
    }

    return $output
}

<#
.SYNOPSIS
    Returns the TelemetryBridge buffer status ('buffer status' passthrough).

.EXAMPLE
    Get-TelemetryBufferStatus
#>
function Get-TelemetryBufferStatus {
    [CmdletBinding()]
    [OutputType([string])]
    param([string]$CliPath)

    $result = Invoke-TelemetryBridgeCli -Arguments @('buffer', 'status') -CliPath $CliPath
    if ($result.ExitCode -ne 0) {
        throw "telemetrybridge 'buffer status' exited with code $($result.ExitCode).`n$($result.Output)"
    }

    return $result.Output.TrimEnd()
}

Export-ModuleMember -Function Invoke-WithTelemetry, Send-TelemetryJob, Get-TelemetryBufferStatus
