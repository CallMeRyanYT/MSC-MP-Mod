[CmdletBinding()]
param(
    [string]$AssemblyPath,
    [string]$GameManaged,
    [string]$CecilPath
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this metadata-only audit with PowerShell 7 (pwsh).' }
$auditRoot = Split-Path -Parent $PSScriptRoot
if (!$AssemblyPath) { $AssemblyPath = Join-Path $auditRoot 'artifacts\current\MSCMP.Mod.dll' }
if (!$GameManaged) {
    . (Join-Path $PSScriptRoot 'common.ps1')
    $GameManaged = (Get-LocalConfiguration).Managed
}
if (!$CecilPath) {
    # Optional local inspection tool, never included in the mod or player ZIP.
    $toolRoot = Join-Path $auditRoot '.local\tools\.store'
    if (Test-Path -LiteralPath $toolRoot) {
        $cecil = Get-ChildItem -LiteralPath $toolRoot -Filter Mono.Cecil.dll -Recurse -File | Select-Object -First 1
        if ($cecil) { $CecilPath = $cecil.FullName }
    }
}
if (!$CecilPath -or !(Test-Path -LiteralPath $CecilPath)) {
    throw 'Supply -CecilPath to a local Mono.Cecil.dll (for example from the local ILSpy tool). No game assemblies are downloaded.'
}
Add-Type -Path $CecilPath
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($search in @($resolver.GetSearchDirectories())) { $resolver.RemoveSearchDirectory($search) }
$managedPath = [IO.Path]::GetFullPath($GameManaged).TrimEnd('\','/')
$resolver.AddSearchDirectory($managedPath)
$parameters = New-Object Mono.Cecil.ReaderParameters
$parameters.AssemblyResolver = $resolver
$module = $null
try {
    $module = [Mono.Cecil.ModuleDefinition]::ReadModule([IO.Path]::GetFullPath($AssemblyPath), $parameters)
    # Verify resolution uses the shipped files, never a desktop/GAC substitute.
    foreach ($reference in $module.AssemblyReferences) {
        $resolved = $resolver.Resolve($reference)
        $expected = Join-Path $managedPath ($reference.Name + '.dll')
        if (![String]::Equals([IO.Path]::GetFullPath($resolved.MainModule.FileName), $expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('Reference resolved outside game Managed directory: ' + $reference.Name)
        }
    }
    $missing = New-Object 'System.Collections.Generic.List[string]'
    $types = @($module.GetTypeReferences())
    $members = @($module.GetMemberReferences())
    foreach ($reference in $types) {
        try { if (!$reference.Resolve()) { $missing.Add('Type: ' + $reference.FullName) } }
        catch { $missing.Add('Type: ' + $reference.FullName + ' (' + $_.Exception.Message + ')') }
    }
    foreach ($reference in $members) {
        try { if (!$reference.Resolve()) { $missing.Add('Member: ' + $reference.FullName) } }
        catch { $missing.Add('Member: ' + $reference.FullName + ' (' + $_.Exception.Message + ')') }
    }
    if ($missing.Count) { throw ("Shipped runtime API audit failed:`n" + [String]::Join("`n", $missing.ToArray())) }
    Write-Host ('PASS: ' + $types.Count + ' type references and ' + $members.Count + ' member references resolve against the shipped game libraries.')
    Write-Host 'Metadata only: reflection-created calls, Mono execution, assembly-binding policy and multiplayer behavior still need runtime tests.'
} finally {
    if ($module) { $module.Dispose() }
    $resolver.Dispose()
}
