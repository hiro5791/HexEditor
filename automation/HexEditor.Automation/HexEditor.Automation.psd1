@{
    RootModule        = 'HexEditor.Automation.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = '4b3f6c55-7d0e-4c1a-9d7e-2f1f5b0e6a51'
    Author            = 'HexEditor'
    Description       = 'Automates the running HexEditor through JSON-RPC over a named pipe (AUTO-45).'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport = @('Get-HexEditorPipeName', 'Connect-HexEditor', 'Disconnect-HexEditor', 'Invoke-HexEditorMethod', 'Get-HexEditorDocument', 'Open-HexEditorFile', 'Read-HexEditorBytes')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
