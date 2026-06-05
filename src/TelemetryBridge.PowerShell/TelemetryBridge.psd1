@{
    RootModule        = 'TelemetryBridge.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '86d6f29d-9120-4fef-b05b-4cb3a38f5517'
    Author            = 'Levitec'
    CompanyName       = 'Levitec'
    Copyright         = '(c) Levitec. All rights reserved.'
    Description       = 'Ergonomic PowerShell wrapper over the TelemetryBridge CLI. Wrap any job in Invoke-WithTelemetry to record its outcome, duration, and errors without changing the job''s own success/failure.'

    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Core', 'Desktop')

    FunctionsToExport = @('Invoke-WithTelemetry', 'Send-TelemetryJob', 'Get-TelemetryBufferStatus')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags         = @('telemetry', 'observability', 'opentelemetry', 'otlp', 'scriptrunner', 'automation')
            ProjectUri   = 'https://github.com/levitec/TelemetryBridge'
            ReleaseNotes = 'Initial release: Invoke-WithTelemetry, Send-TelemetryJob, Get-TelemetryBufferStatus.'
        }
    }
}
