<#
.SYNOPSIS
    Verifies (and for legacy Visual Studio Installer Projects MSIs, patches) the uninstall custom action in the built
    StartupController MSI (D-T3, D-T5).

.DESCRIPTION
    The WiX setup project (SetupStartupController.wixproj) authors the custom action correctly and runs this script
    with -Verify after every build; a failed check deletes the MSI there.

    Without -Verify (the former vdproj PostBuildEvent): Visual Studio Installer Projects mark custom actions deferred and
    NoImpersonate (0x800), so StartupController.ReturnToWindows.exe would run as LocalSystem and see the wrong HKCU.
    This script finds the helper's CustomAction row by its source File key, clears 0x800 (so it impersonates the
    uninstalling user) and sets 0x40 (the exit code is ignored, so the helper can never block an uninstall). It also
    adds RETURNTOWINDOWS to SecureCustomProperties, so the property set on an elevated command line reaches the
    custom action. Then it re-opens the MSI read-only and verifies the Type, the arguments and the condition.

    On any failure the built MSI is deleted, an MSBuild-format "error :" line is written and the script exits 1, so
    the build fails and an unpatched MSI is never left in the output folder.

.PARAMETER Msi
    Path of the built MSI.

.PARAMETER Verify
    Only check a built MSI (the WiX build, and manual check M-T1). Never modifies or deletes it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Msi,

    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$HelperFileName = 'StartupController.ReturnToWindows.exe'
$ExpectedCondition = 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE'
$ExpectedArguments = @('--uilevel [UILevel]', '--choice "[RETURNTOWINDOWS]"')
$SecureProperty = 'RETURNTOWINDOWS'

# CustomAction.Type bits (msidbCustomActionType*)
$TypeExeFromFile = 0x12     # Exe (0x2) + SourceFile (0x10), the low 6 bits
$TypeContinue = 0x40        # ignore the exit code
$TypeRollback = 0x100       # runs only on rollback
$TypeCommit = 0x200         # runs only on commit
$TypeInScript = 0x400       # deferred
$TypeNoImpersonate = 0x800  # run as LocalSystem

$OpenReadOnly = 0
$OpenTransact = 1

$script:ComObjects = New-Object System.Collections.ArrayList

function Track($obj) {
    if ($null -ne $obj) { [void]$script:ComObjects.Add($obj) }
    return $obj
}

# Releases every COM object (views, records, databases) so the MSI file is closed before it is verified or deleted
function Clear-ComObjects {
    for ($i = $script:ComObjects.Count - 1; $i -ge 0; $i--) {
        try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($script:ComObjects[$i]) } catch { }
    }
    $script:ComObjects.Clear()
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

function Invoke-Member($obj, [string]$name, [System.Reflection.BindingFlags]$kind, [object[]]$arguments) {
    return $obj.GetType().InvokeMember($name, $kind, $null, $obj, $arguments)
}

function Open-Database([string]$path, [int]$mode) {
    $installer = Track (New-Object -ComObject WindowsInstaller.Installer)
    return Track (Invoke-Member $installer 'OpenDatabase' InvokeMethod @($path, $mode))
}

# Every row of a query as a list of string[] (one string per column). Returned as ONE object (unary comma) and only
# ever walked with foreach: piping or @() would unroll a single row into its column values.
function Get-Rows($db, [string]$sql, [int]$columns) {
    $view = Track (Invoke-Member $db 'OpenView' InvokeMethod @($sql))
    [void](Invoke-Member $view 'Execute' InvokeMethod $null)
    $rows = New-Object 'System.Collections.Generic.List[string[]]'
    while ($true) {
        $record = Invoke-Member $view 'Fetch' InvokeMethod $null
        if ($null -eq $record) { break }
        [void](Track $record)
        $values = New-Object 'string[]' $columns
        for ($c = 1; $c -le $columns; $c++) {
            $values[$c - 1] = [string](Invoke-Member $record 'StringData' GetProperty @([int]$c))
        }
        $rows.Add($values)
    }
    [void](Invoke-Member $view 'Close' InvokeMethod $null)
    return , $rows
}

function Invoke-Sql($db, [string]$sql) {
    $view = Track (Invoke-Member $db 'OpenView' InvokeMethod @($sql))
    [void](Invoke-Member $view 'Execute' InvokeMethod $null)
    [void](Invoke-Member $view 'Close' InvokeMethod $null)
}

# MSI SQL has no escaping, so only plain identifiers and property lists are ever put into a query
function Assert-SqlSafe([string]$value, [string]$what) {
    if ($value -notmatch '^[A-Za-z0-9_.;]+$') { throw "$what '$value' contains unexpected characters" }
}

# The helper's CustomAction row: exactly one exe custom action whose source is the helper's File row
function Find-HelperAction($db) {
    $fileKeys = New-Object 'System.Collections.Generic.List[string]'
    foreach ($row in (Get-Rows $db 'SELECT `File`, `FileName` FROM `File`' 2)) {
        if (($row[1] -split '\|')[-1] -ieq $HelperFileName) { $fileKeys.Add($row[0]) }
    }
    if ($fileKeys.Count -ne 1) { throw "expected 1 File row for $HelperFileName, found $($fileKeys.Count)" }

    $actions = New-Object 'System.Collections.Generic.List[string[]]'
    foreach ($row in (Get-Rows $db 'SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`' 4)) {
        if ($row[2] -ceq $fileKeys[0] -and (([int]$row[1]) -band 0x3F) -eq $TypeExeFromFile) { $actions.Add($row) }
    }
    if ($actions.Count -ne 1) { throw "expected 1 custom action running $HelperFileName, found $($actions.Count)" }

    $action = $actions[0]
    Assert-SqlSafe $action[0] 'Custom action name'
    return [pscustomobject]@{ Action = $action[0]; Type = [int]$action[1]; Target = $action[3] }
}

function Get-SecureCustomProperties($db) {
    $rows = Get-Rows $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'SecureCustomProperties'" 1
    if ($rows.Count -eq 0) { return $null }
    return $rows[0][0]
}

# True for exactly $ExpectedCondition, "($ExpectedCondition) AND <component test>" or "<component test> AND
# ($ExpectedCondition)", where the component test is "$<component key>=2" (the component is being removed).
# The input has its whitespace collapsed to single spaces.
function Test-UninstallCondition([string]$condition) {
    if ($condition -ceq $ExpectedCondition) { return $true }
    $componentTest = '\$[A-Za-z_][A-Za-z0-9_.]* ?= ?2'
    $expected = [regex]::Escape("($ExpectedCondition)")
    return ($condition -cmatch "^$expected AND $componentTest$") -or ($condition -cmatch "^$componentTest AND $expected$")
}

function Get-SequenceNumber([string]$text, [string]$what) {
    $value = 0
    if (-not [int]::TryParse($text, [ref]$value)) { throw "$what has no sequence number in InstallExecuteSequence" }
    return $value
}

# Sequence number of a standard action in InstallExecuteSequence, or $null when it isn't there
function Get-StandardSequence($db, [string]$name) {
    $rows = Get-Rows $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = '$name'" 1
    if ($rows.Count -eq 0) { return $null }
    return Get-SequenceNumber $rows[0][0] $name
}

function Invoke-Patch([string]$path) {
    try {
        $db = Open-Database $path $OpenTransact
        $action = Find-HelperAction $db

        $newType = ($action.Type -band (-bnot $TypeNoImpersonate)) -bor $TypeContinue
        if ($newType -ne $action.Type) {
            Invoke-Sql $db "UPDATE ``CustomAction`` SET ``Type`` = $newType WHERE ``Action`` = '$($action.Action)'"
        }

        $secure = Get-SecureCustomProperties $db
        if ($null -eq $secure) {
            Invoke-Sql $db "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('SecureCustomProperties', '$SecureProperty')"
        }
        elseif (($secure -split ';') -notcontains $SecureProperty) {
            $value = "$secure;$SecureProperty"
            Assert-SqlSafe $value 'SecureCustomProperties'
            Invoke-Sql $db "UPDATE ``Property`` SET ``Value`` = '$value' WHERE ``Property`` = 'SecureCustomProperties'"
        }

        [void](Invoke-Member $db 'Commit' InvokeMethod $null)
        Write-Output "Patch-UninstallCustomAction: $($action.Action) Type $($action.Type) -> $newType"
    }
    finally {
        Clear-ComObjects
    }
}

function Invoke-Verify([string]$path) {
    try {
        $db = Open-Database $path $OpenReadOnly
        $action = Find-HelperAction $db

        if (($action.Type -band $TypeNoImpersonate) -ne 0) { throw "custom action Type $($action.Type) still has NoImpersonate (0x800)" }
        if (($action.Type -band $TypeContinue) -eq 0) { throw "custom action Type $($action.Type) doesn't ignore the exit code (0x40)" }
        if (($action.Type -band $TypeInScript) -eq 0) { throw "custom action Type $($action.Type) is not deferred (0x400)" }
        # A rollback or commit action would never run on a plain uninstall (vdproj Rollback/Commit custom actions)
        if (($action.Type -band ($TypeRollback -bor $TypeCommit)) -ne 0) { throw "custom action Type $($action.Type) is a rollback or commit action (0x100/0x200), not an uninstall action" }

        foreach ($argument in $ExpectedArguments) {
            if (-not $action.Target.Contains($argument)) { throw "custom action arguments '$($action.Target)' lack '$argument'" }
        }

        $rows = Get-Rows $db "SELECT ``Condition``, ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = '$($action.Action)'" 2
        if ($rows.Count -ne 1) { throw "expected 1 InstallExecuteSequence row for $($action.Action), found $($rows.Count)" }
        # The condition limits the action to a full uninstall that is not part of a major upgrade. vdproj may combine it
        # with a component state test, so (whitespace collapsed) it must be exactly the expected condition, or the
        # expected condition in parentheses ANDed with one component test ($Component=2, either order). Anything else
        # (NOT (...), ... OR ..., extra terms) is rejected.
        $condition = ($rows[0][0] -replace '\s+', ' ').Trim()
        if (-not (Test-UninstallCondition $condition)) { throw "custom action condition '$condition' is not '$ExpectedCondition' (optionally with one component test)" }

        # Deferred: it must sit inside the script (InstallInitialize..InstallFinalize) and run before RemoveFiles, while
        # the helper exe still exists
        $sequence = Get-SequenceNumber $rows[0][1] $action.Action
        $initialize = Get-StandardSequence $db 'InstallInitialize'
        $finalize = Get-StandardSequence $db 'InstallFinalize'
        $removeFiles = Get-StandardSequence $db 'RemoveFiles'
        if ($null -eq $initialize -or $null -eq $finalize) { throw "InstallExecuteSequence lacks InstallInitialize or InstallFinalize" }
        if ($sequence -le $initialize -or $sequence -ge $finalize) { throw "custom action sequence $sequence is outside InstallInitialize ($initialize) .. InstallFinalize ($finalize)" }
        if ($null -ne $removeFiles -and $sequence -ge $removeFiles) { throw "custom action sequence $sequence is not before RemoveFiles ($removeFiles), so the helper would already be deleted" }

        $secure = Get-SecureCustomProperties $db
        if ($null -eq $secure -or ($secure -split ';') -notcontains $SecureProperty) { throw "SecureCustomProperties doesn't list $SecureProperty" }

        Write-Output "Patch-UninstallCustomAction: verified $($action.Action) (Type $($action.Type), sequence $sequence, condition '$condition')"
    }
    finally {
        Clear-ComObjects
    }
}

$msiPath = $null
try {
    $msiPath = (Resolve-Path -LiteralPath $Msi).ProviderPath
    if (-not $Verify) {
        Invoke-Patch $msiPath
    }
    Invoke-Verify $msiPath
    exit 0
}
catch {
    $reason = $_.Exception.Message
    Clear-ComObjects
    if (-not $Verify -and $null -ne $msiPath -and (Test-Path -LiteralPath $msiPath)) {
        try {
            Remove-Item -LiteralPath $msiPath -Force
            $reason += " (the MSI was deleted, so no unverified MSI is left behind)"
        }
        catch {
            $reason += " (the MSI could not be deleted: $($_.Exception.Message); do not ship it)"
        }
    }
    # MSBuild error format, so Visual Studio shows it in the Error List
    [Console]::Out.WriteLine("error : Patch-UninstallCustomAction: $reason")
    exit 1
}
