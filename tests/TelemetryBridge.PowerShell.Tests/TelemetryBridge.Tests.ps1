#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

<#
    Pester v5 tests for the TelemetryBridge PowerShell module.

    Strategy: every CLI call funnels through the private `Invoke-TelemetryBridgeCli`
    function, so we mock that single seam (-ModuleName TelemetryBridge) and assert
    on the exact argument vector the cmdlets build. No real CLI process is launched.

    Note: -ParameterFilter blocks execute inside the module's session state, so they
    use only built-in operators / .NET statics (no test-script helper functions).
    `[array]::IndexOf($Arguments, '--flag')` locates a flag; the value is the next
    element. Repeated `--attr key=value` pairs are checked with -contains.
#>

BeforeAll {
    $modulePath = Join-Path $PSScriptRoot '..\..\src\TelemetryBridge.PowerShell\TelemetryBridge.psd1'
    Import-Module $modulePath -Force
}

AfterAll {
    Remove-Module TelemetryBridge -Force -ErrorAction SilentlyContinue
}

Describe 'Invoke-WithTelemetry' {

    BeforeEach {
        # Default happy-path stub: CLI exits 0 and produces no meaningful output.
        Mock -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli {
            [pscustomobject]@{ ExitCode = 0; Output = ''; Command = 'stub' }
        }
    }

    It 'returns the script block output unchanged' {
        $result = Invoke-WithTelemetry -JobName 'J' -ScriptBlock { 'hello-world' }
        $result | Should -Be 'hello-world'
    }

    It 'returns multi-item script block output' {
        $result = Invoke-WithTelemetry -JobName 'J' -ScriptBlock { 1; 2; 3 }
        $result | Should -Be @(1, 2, 3)
    }

    It 're-throws the original failure so the job outcome is unchanged' {
        { Invoke-WithTelemetry -JobName 'J' -ScriptBlock { throw 'boom' } } |
            Should -Throw -ExpectedMessage 'boom'
    }

    It 'sends status=succeeded when the block succeeds' {
        Invoke-WithTelemetry -JobName 'J' -ScriptBlock { 'ok' } | Out-Null

        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly -ParameterFilter {
            $i = [array]::IndexOf($Arguments, '--status')
            ($i -ge 0) -and ($Arguments[$i + 1] -eq 'succeeded') -and ($Arguments -notcontains '--error-message')
        }
    }

    It 'sends status=failed with the error message on exception' {
        try { Invoke-WithTelemetry -JobName 'J' -ScriptBlock { throw 'kaboom' } } catch { }

        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly -ParameterFilter {
            $si = [array]::IndexOf($Arguments, '--status')
            $ei = [array]::IndexOf($Arguments, '--error-message')
            ($si -ge 0) -and ($Arguments[$si + 1] -eq 'failed') -and
            ($ei -ge 0) -and ($Arguments[$ei + 1] -eq 'kaboom')
        }
    }

    It 'still records telemetry even though it re-throws' {
        try { Invoke-WithTelemetry -JobName 'J' -ScriptBlock { throw 'x' } } catch { }
        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly
    }

    It 'maps -Attributes to one repeated --attr key=value per entry' {
        Invoke-WithTelemetry -JobName 'J' -Attributes @{ env = 'prod'; region = 'eu' } -ScriptBlock { } | Out-Null

        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly -ParameterFilter {
            $attrCount = ($Arguments | Where-Object { $_ -eq '--attr' }).Count
            ($attrCount -eq 2) -and ($Arguments -contains 'env=prod') -and ($Arguments -contains 'region=eu')
        }
    }

    It 'passes the duration in milliseconds' {
        Invoke-WithTelemetry -JobName 'J' -ScriptBlock { Start-Sleep -Milliseconds 20 } | Out-Null

        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly -ParameterFilter {
            $i = [array]::IndexOf($Arguments, '--duration-ms')
            ($i -ge 0) -and ([long]$Arguments[$i + 1] -ge 0)
        }
    }

    It 'does not let a telemetry failure change a successful job outcome (non-strict)' {
        Mock -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli {
            [pscustomobject]@{ ExitCode = 1; Output = 'cli blew up'; Command = 'stub' }
        }

        # CLI failure -> Send-TelemetryJob throws -> swallowed with a warning; the
        # block output is still returned.
        $result = Invoke-WithTelemetry -JobName 'J' -ScriptBlock { 'survived' } -WarningAction SilentlyContinue
        $result | Should -Be 'survived'
    }

    It 'surfaces a telemetry export failure in -Strict mode when the block succeeded' {
        Mock -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli {
            [pscustomobject]@{ ExitCode = 3; Output = 'export failed'; Command = 'stub' }
        }

        { Invoke-WithTelemetry -JobName 'J' -Strict -ScriptBlock { 'ok' } -WarningAction SilentlyContinue } |
            Should -Throw
    }

    It 'passes --strict to the CLI when -Strict is set' {
        Invoke-WithTelemetry -JobName 'J' -Strict -ScriptBlock { } -WarningAction SilentlyContinue | Out-Null

        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly -ParameterFilter {
            $Arguments -contains '--strict'
        }
    }
}

Describe 'Send-TelemetryJob' {

    BeforeEach {
        Mock -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli {
            [pscustomobject]@{ ExitCode = 0; Output = 'Buffered event: ...'; Command = 'stub' }
        }
    }

    It 'builds a send job invocation with the required flags' {
        Send-TelemetryJob -JobName 'Sync-Users' -Status 'succeeded' -System 'powershell' | Out-Null

        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly -ParameterFilter {
            $ji = [array]::IndexOf($Arguments, '--job-name')
            $sti = [array]::IndexOf($Arguments, '--status')
            $syi = [array]::IndexOf($Arguments, '--system')
            ($Arguments[0] -eq 'send') -and ($Arguments[1] -eq 'job') -and
            ($Arguments[$ji + 1] -eq 'Sync-Users') -and
            ($Arguments[$sti + 1] -eq 'succeeded') -and
            ($Arguments[$syi + 1] -eq 'powershell')
        }
    }

    It 'forwards optional scalar flags only when provided' {
        Send-TelemetryJob -JobName 'J' -DurationMs 1234 -ExitCode 0 -CorrelationId 'abc' | Out-Null

        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly -ParameterFilter {
            $di = [array]::IndexOf($Arguments, '--duration-ms')
            $ei = [array]::IndexOf($Arguments, '--exit-code')
            $ci = [array]::IndexOf($Arguments, '--correlation-id')
            ($Arguments[$di + 1] -eq '1234') -and
            ($Arguments[$ei + 1] -eq '0') -and
            ($Arguments[$ci + 1] -eq 'abc') -and
            ($Arguments -notcontains '--instance')
        }
    }

    It 'maps -Attributes to repeated --attr args' {
        Send-TelemetryJob -JobName 'J' -Attributes @{ a = '1'; b = '2' } | Out-Null

        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly -ParameterFilter {
            $attrCount = ($Arguments | Where-Object { $_ -eq '--attr' }).Count
            ($attrCount -eq 2) -and ($Arguments -contains 'a=1') -and ($Arguments -contains 'b=2')
        }
    }

    It 'throws when the CLI exits non-zero' {
        Mock -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli {
            [pscustomobject]@{ ExitCode = 3; Output = 'export failed'; Command = 'stub' }
        }

        { Send-TelemetryJob -JobName 'J' -Strict } | Should -Throw -ExpectedMessage '*exited with code 3*'
    }

    It 'rejects an invalid status via ValidateSet' {
        { Send-TelemetryJob -JobName 'J' -Status 'not-a-real-status' } | Should -Throw
    }
}

Describe 'Get-TelemetryBufferStatus' {

    It 'invokes buffer status and returns its output' {
        Mock -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli {
            [pscustomobject]@{ ExitCode = 0; Output = "Buffer status:`n  pending: 0`n"; Command = 'stub' }
        }

        $out = Get-TelemetryBufferStatus
        $out | Should -BeLike '*Buffer status*'

        Should -Invoke -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli -Times 1 -Exactly -ParameterFilter {
            ($Arguments[0] -eq 'buffer') -and ($Arguments[1] -eq 'status')
        }
    }

    It 'throws when buffer status exits non-zero' {
        Mock -ModuleName TelemetryBridge Invoke-TelemetryBridgeCli {
            [pscustomobject]@{ ExitCode = 1; Output = 'boom'; Command = 'stub' }
        }
        { Get-TelemetryBufferStatus } | Should -Throw
    }
}
