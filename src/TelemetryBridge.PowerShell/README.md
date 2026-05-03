# TelemetryBridge.PowerShell

Planned PowerShell wrapper module.

Example target API:
```powershell
Invoke-WithTelemetry -JobName "Sync-Users" -System "powershell" -ScriptBlock {
    .\Sync-Users.ps1
}
```
