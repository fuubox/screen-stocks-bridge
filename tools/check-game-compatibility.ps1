param(
    [string]$GameDir = $(if ($env:SCREENSTOCKS_GAME_DIR) { $env:SCREENSTOCKS_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Screen Stocks Demo' })
)

$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot 'game-compatibility.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$gameAssemblyPath = Join-Path $GameDir 'Screen Stocks_Data\Managed\Assembly-CSharp.dll'
$bepInExPath = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
$harmonyPath = Join-Path $GameDir 'BepInEx\core\0Harmony.dll'
$cecilPath = Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll'

foreach ($path in @($gameAssemblyPath, $bepInExPath, $harmonyPath, $cecilPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required compatibility-check file is missing: $path"
    }
}

Add-Type -Path $cecilPath
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)
$hash = (Get-FileHash -LiteralPath $gameAssemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$bepInExVersion = [System.Reflection.AssemblyName]::GetAssemblyName($bepInExPath).Version.ToString()
$harmonyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($harmonyPath).Version.ToString()

$observed = [ordered]@{
    assemblyName = $assembly.Name.Name
    assemblyVersion = $assembly.Name.Version.ToString()
    moduleMvid = $assembly.MainModule.Mvid.ToString()
    sha256 = $hash
    bepInExVersion = $bepInExVersion
    harmonyVersion = $harmonyVersion
}
foreach ($key in $observed.Keys) {
    if ([string]$manifest.$key -cne [string]$observed[$key]) {
        throw "Game compatibility fingerprint changed for '$key'. Expected '$($manifest.$key)', found '$($observed[$key])'. Review the game patch and update tools/game-compatibility.json only after validating compatibility."
    }
}

foreach ($contract in $manifest.methods) {
    $type = $assembly.MainModule.GetType([string]$contract.type)
    if ($null -eq $type) { throw "Required game type is missing: $($contract.type)" }
    $expectedParameters = @($contract.parameters)
    $matching = @($type.Methods | Where-Object {
        if ($_.Name -cne [string]$contract.name -or $_.ReturnType.FullName -cne [string]$contract.returnType) { return $false }
        $actualParameters = @($_.Parameters | ForEach-Object { $_.ParameterType.FullName })
        if ($actualParameters.Count -ne $expectedParameters.Count) { return $false }
        for ($i = 0; $i -lt $actualParameters.Count; $i++) {
            if ($actualParameters[$i] -cne [string]$expectedParameters[$i]) { return $false }
        }
        return $true
    })
    if ($matching.Count -ne 1) {
        throw "Expected exactly one matching signature for $($contract.type).$($contract.name); found $($matching.Count)."
    }
}

foreach ($contract in $manifest.fields) {
    $type = $assembly.MainModule.GetType([string]$contract.type)
    if ($null -eq $type) { throw "Required game type is missing: $($contract.type)" }
    $matching = @($type.Fields | Where-Object {
        $_.Name -ceq [string]$contract.name -and
        $_.FieldType.FullName -ceq [string]$contract.fieldType -and
        $_.IsPublic -eq [bool]$contract.public
    })
    if ($matching.Count -ne 1) {
        throw "Expected exactly one matching field $($contract.type).$($contract.name); found $($matching.Count)."
    }
}

Write-Host "Compatible game surface: $($manifest.game)"
Write-Host "Assembly-CSharp version $($observed.assemblyVersion), MVID $($observed.moduleMvid), SHA-256 $($observed.sha256)"
Write-Host "BepInEx $bepInExVersion; Harmony $harmonyVersion"
