# HexEditor.Automation: calls the running HexEditor through JSON-RPC over a named pipe (AUTO-45).
# Works with Windows PowerShell 5.1 and PowerShell 7. Turn on "Settings > Automation > External automation" in HexEditor first.
# The first connection from powershell.exe / pwsh.exe shows a confirmation in HexEditor.
#
# Example:
#   Import-Module HexEditor.Automation
#   Get-HexEditorDocument
#   $doc = Open-HexEditorFile -Path C:\data\sample.bin
#   Read-HexEditorBytes -Document $doc.id -Offset 0x10 -Length 16
#   Invoke-HexEditorMethod -Method 'document.goTo' -Params @($doc.id, 'end-0x10')

Set-StrictMode -Version 2.0

$script:Connection = $null
$script:NextId = 0

function Get-HexEditorPipeName {
    <#
    .SYNOPSIS
    Returns the pipe name of the current user (HexEditor.<SID>.rpc).
    #>
    [CmdletBinding()]
    param()
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    "HexEditor.$sid.rpc"
}

function Connect-HexEditor {
    <#
    .SYNOPSIS
    Connects to the running HexEditor. Other commands connect automatically when no connection is given.
    #>
    [CmdletBinding()]
    param(
        [string] $PipeName = (Get-HexEditorPipeName),
        [int] $TimeoutMilliseconds = 5000
    )
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect($TimeoutMilliseconds)
    }
    catch {
        $pipe.Dispose()
        throw "Cannot connect to HexEditor ($PipeName). Is external automation turned on? $($_.Exception.Message)"
    }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $connection = [pscustomobject]@{
        Pipe   = $pipe
        Reader = New-Object System.IO.StreamReader($pipe, $encoding, $false, 65536, $true)
        Writer = New-Object System.IO.StreamWriter($pipe, $encoding, 65536, $true)
    }
    $connection.Writer.NewLine = "`n"
    $connection.Writer.AutoFlush = $true
    $script:Connection = $connection
    $connection
}

function Disconnect-HexEditor {
    <#
    .SYNOPSIS
    Closes the connection.
    #>
    [CmdletBinding()]
    param([object] $Connection = $script:Connection)
    if ($null -ne $Connection) {
        $Connection.Writer.Dispose()
        $Connection.Reader.Dispose()
        $Connection.Pipe.Dispose()
        if ($Connection -eq $script:Connection) { $script:Connection = $null }
    }
}

function Invoke-HexEditorMethod {
    <#
    .SYNOPSIS
    Calls a JSON-RPC method (for example app.open, document.readBytes). Params is an array (positional) or a hashtable (named).
    Byte arrays are Base64 text. Errors become terminating errors with the JSON-RPC error code.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Method,
        [object] $Params = $null,
        [object] $Connection = $script:Connection
    )
    if ($null -eq $Connection) { $Connection = Connect-HexEditor }
    $script:NextId++
    $request = [ordered]@{ jsonrpc = '2.0'; id = $script:NextId; method = $Method }
    if ($null -ne $Params) { $request.params = $Params }
    $json = ConvertTo-Json -InputObject $request -Compress -Depth 20
    $Connection.Writer.WriteLine($json)
    while ($true) {
        $line = $Connection.Reader.ReadLine()
        if ($null -eq $line) { throw 'HexEditor closed the connection.' }
        $message = ConvertFrom-Json -InputObject $line
        if (-not ($message.PSObject.Properties.Name -contains 'id')) { continue }   # notification
        if ($null -ne $message.id -and $message.id -ne $script:NextId) { continue }
        if ($message.PSObject.Properties.Name -contains 'error') {
            throw "HexEditor error $($message.error.code): $($message.error.message)"
        }
        return $message.result
    }
}

function Get-HexEditorDocument {
    <#
    .SYNOPSIS
    Lists the open documents (id, kind, path, name, length, isModified, isReadOnly).
    #>
    [CmdletBinding()]
    param([object] $Connection = $script:Connection)
    $documents = Invoke-HexEditorMethod -Method 'app.documents' -Connection $Connection
    foreach ($d in $documents) { $d }
}

function Open-HexEditorFile {
    <#
    .SYNOPSIS
    Opens a file in HexEditor and returns the document.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [switch] $ReadOnly,
        [object] $Connection = $script:Connection
    )
    $full = (Resolve-Path -LiteralPath $Path).ProviderPath
    Invoke-HexEditorMethod -Method 'app.open' -Params @($full, @{ readOnly = [bool]$ReadOnly }) -Connection $Connection
}

function Read-HexEditorBytes {
    <#
    .SYNOPSIS
    Reads bytes from a document (at most 16 MB per call) and returns a byte array.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Document,
        [Parameter(Mandatory = $true)] [object] $Offset,
        [Parameter(Mandatory = $true)] [object] $Length,
        [object] $Connection = $script:Connection
    )
    $base64 = Invoke-HexEditorMethod -Method 'document.readBytes' -Params @($Document, $Offset, $Length) -Connection $Connection
    , [System.Convert]::FromBase64String($base64)
}

Export-ModuleMember -Function Get-HexEditorPipeName, Connect-HexEditor, Disconnect-HexEditor, Invoke-HexEditorMethod, Get-HexEditorDocument, Open-HexEditorFile, Read-HexEditorBytes
