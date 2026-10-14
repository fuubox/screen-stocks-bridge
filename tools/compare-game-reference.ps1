param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Initialize', 'Compare', 'Promote')]
    [string]$Mode,
    [string]$GameDir = $(if ($env:SCREENSTOCKS_GAME_DIR) { $env:SCREENSTOCKS_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Screen Stocks Demo' })
)

$ErrorActionPreference = 'Stop'
$referenceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) '.local\game-reference'
$baselineDir = Join-Path $referenceRoot 'baseline'
$candidateDir = Join-Path $referenceRoot 'candidate'
$diffPath = Join-Path $referenceRoot 'latest-diff.txt'
$cecilRelativePath = 'BepInEx\core\Mono.Cecil.dll'
$referencePaths = @(
    'BepInEx\core\BepInEx.dll',
    'BepInEx\core\0Harmony.dll',
    'BepInEx\core\Mono.Cecil.dll',
    'Screen Stocks_Data\Managed\Assembly-CSharp.dll',
    'Screen Stocks_Data\Managed\UnityEngine.CoreModule.dll',
    'Screen Stocks_Data\Managed\UnityEngine.dll',
    'Screen Stocks_Data\Managed\UnityEngine.JSONSerializeModule.dll',
    'Screen Stocks_Data\Managed\UnityEngine.IMGUIModule.dll',
    'Screen Stocks_Data\Managed\UnityEngine.TextRenderingModule.dll'
)

function Get-AllCecilTypes($type) {
    ,$type
    foreach ($nested in $type.NestedTypes) {
        Get-AllCecilTypes $nested
    }
}

function Get-Visibility($member) {
    if ($member.IsPublic) { return 'public' }
    if ($member.IsFamilyOrAssembly) { return 'protected-internal' }
    if ($member.IsFamilyAndAssembly) { return 'private-protected' }
    if ($member.IsFamily) { return 'protected' }
    if ($member.IsAssembly) { return 'internal' }
    return 'private'
}

function Get-AssemblySurfaceLines([string]$assemblyPath, [string]$cecilPath) {
    if ($null -eq ('Mono.Cecil.AssemblyDefinition' -as [type])) { Add-Type -Path $cecilPath }
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyPath)
    try {
        $lines = [System.Collections.Generic.List[string]]::new()
        foreach ($type in ($assembly.MainModule.Types | ForEach-Object { Get-AllCecilTypes $_ })) {
            $typeFlags = @((Get-Visibility $type), $(if ($type.IsAbstract) { 'abstract' }), $(if ($type.IsSealed) { 'sealed' }), $(if ($type.IsInterface) { 'interface' }), $(if ($type.IsEnum) { 'enum' })) | Where-Object { $_ }
            $lines.Add("TYPE $($typeFlags -join ' ') $($type.FullName) : $($type.BaseType.FullName)")

            foreach ($field in $type.Fields) {
                $flags = @((Get-Visibility $field), $(if ($field.IsStatic) { 'static' }), $(if ($field.IsInitOnly) { 'readonly' }), $(if ($field.IsLiteral) { 'const' })) | Where-Object { $_ }
                $lines.Add("FIELD $($type.FullName) [$($flags -join ' ')] $($field.FieldType.FullName) $($field.Name)")
            }

            foreach ($method in $type.Methods) {
                $parameters = @($method.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ', '
                $flags = @((Get-Visibility $method), $(if ($method.IsStatic) { 'static' }), $(if ($method.IsAbstract) { 'abstract' }), $(if ($method.IsVirtual) { 'virtual' }), $(if ($method.IsConstructor) { 'constructor' })) | Where-Object { $_ }
                $lines.Add("METHOD $($type.FullName) [$($flags -join ' ')] $($method.ReturnType.FullName) $($method.Name)($parameters)")
            }

            foreach ($property in $type.Properties) {
                $indexParameters = @($property.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ', '
                $lines.Add("PROPERTY $($type.FullName) $($property.PropertyType.FullName) $($property.Name)($indexParameters) get=$([bool]$property.GetMethod) set=$([bool]$property.SetMethod)")
            }

            foreach ($event in $type.Events) {
                $lines.Add("EVENT $($type.FullName) $($event.EventType.FullName) $($event.Name)")
            }
        }
        return @($lines | Sort-Object -Unique)
    }
    finally {
        $assembly.Dispose()
    }
}

function New-Snapshot([string]$destination) {
    if (-not (Test-Path -LiteralPath $GameDir -PathType Container)) {
        throw "Game directory not found: $GameDir"
    }

    $stagingDir = "$destination.staging"
    if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
    try {
        $records = foreach ($relativePath in $referencePaths) {
            $sourcePath = Join-Path $GameDir $relativePath
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw "Required reference assembly is missing: $sourcePath"
            }
            $destinationPath = Join-Path $stagingDir $relativePath
            New-Item -ItemType Directory -Force -Path (Split-Path $destinationPath -Parent) | Out-Null
            Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
            $identity = [System.Reflection.AssemblyName]::GetAssemblyName($destinationPath)
            [pscustomobject]@{
                path = $relativePath.Replace('\', '/')
                length = (Get-Item -LiteralPath $destinationPath).Length
                sha256 = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
                assemblyName = $identity.Name
                assemblyVersion = $identity.Version.ToString()
            }
        }

        foreach ($relativePath in $referencePaths) {
            $assemblyPath = Join-Path $stagingDir $relativePath
            $surfacePath = [System.IO.Path]::ChangeExtension($assemblyPath, '.api.txt')
            $surface = Get-AssemblySurfaceLines $assemblyPath (Join-Path $stagingDir $cecilRelativePath)
            [System.IO.File]::WriteAllLines($surfacePath, $surface, [System.Text.UTF8Encoding]::new($false))
        }
        $metadata = [ordered]@{
            sourceGameDirectory = $GameDir
            capturedAtUtc = [DateTime]::UtcNow.ToString('o')
            references = @($records)
        } | ConvertTo-Json -Depth 5
        [System.IO.File]::WriteAllText((Join-Path $stagingDir 'snapshot.json'), $metadata + "`n", [System.Text.UTF8Encoding]::new($false))

        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
        Move-Item -LiteralPath $stagingDir -Destination $destination
    }
    catch {
        if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
        throw
    }
}

New-Item -ItemType Directory -Force -Path $referenceRoot | Out-Null

switch ($Mode) {
    'Initialize' {
        if (Test-Path -LiteralPath $baselineDir) {
            throw "A local baseline already exists at $baselineDir. Use -Mode Compare to inspect a patch, or -Mode Promote after reviewing it."
        }
        New-Snapshot $baselineDir
        Write-Host "Initialized local game reference baseline: $baselineDir"
        Write-Host 'Game and BepInEx assemblies are ignored by Git and are not included in release packages.'
    }
    'Compare' {
        if (-not (Test-Path -LiteralPath (Join-Path $baselineDir 'snapshot.json'))) {
            throw "No baseline exists at $baselineDir. Run this script with -Mode Initialize first."
        }
        New-Snapshot $candidateDir
        $baseline = Get-Content -LiteralPath (Join-Path $baselineDir 'snapshot.json') -Raw | ConvertFrom-Json
        $candidate = Get-Content -LiteralPath (Join-Path $candidateDir 'snapshot.json') -Raw | ConvertFrom-Json
        $report = [System.Collections.Generic.List[string]]::new()
        $report.Add("Game reference comparison: $([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz'))")
        $report.Add("Baseline captured: $($baseline.capturedAtUtc) from $($baseline.sourceGameDirectory)")
        $report.Add("Candidate captured: $($candidate.capturedAtUtc) from $($candidate.sourceGameDirectory)")
        $report.Add('')
        $report.Add('Assembly file changes (binary hashes and identities):')
        foreach ($currentRef in $candidate.references) {
            $oldRef = @($baseline.references | Where-Object path -CEQ $currentRef.path)[0]
            if ($null -eq $oldRef) {
                $report.Add("+ $($currentRef.path) [new reference]")
            }
            elseif ($oldRef.sha256 -cne $currentRef.sha256) {
                $report.Add("~ $($currentRef.path): $($oldRef.assemblyVersion) $($oldRef.sha256) -> $($currentRef.assemblyVersion) $($currentRef.sha256)")
            }
            else {
                $report.Add("= $($currentRef.path): unchanged ($($currentRef.sha256))")
            }
        }
        $report.Add('')
        $report.Add('Managed type/member surface changes (+ added, - removed):')
        $surfaceChangeCount = 0
        foreach ($relativePath in $referencePaths) {
            $baselineAssembly = Join-Path $baselineDir $relativePath
            $candidateAssembly = Join-Path $candidateDir $relativePath
            $oldSurfacePath = [System.IO.Path]::ChangeExtension($baselineAssembly, '.api.txt')
            $newSurfacePath = [System.IO.Path]::ChangeExtension($candidateAssembly, '.api.txt')
            $oldSurface = Get-Content -LiteralPath $oldSurfacePath
            $newSurface = Get-Content -LiteralPath $newSurfacePath
            $surfaceDiff = @(Compare-Object -ReferenceObject $oldSurface -DifferenceObject $newSurface | Sort-Object InputObject, SideIndicator)
            if ($surfaceDiff.Count -gt 0) {
                $report.Add($relativePath.Replace('\', '/'))
                foreach ($change in $surfaceDiff) {
                    $report.Add("$($change.SideIndicator) $($change.InputObject)")
                    $surfaceChangeCount++
                }
            }
        }
        if ($surfaceChangeCount -eq 0) { $report.Add('(no type/member signature changes)') }

        [System.IO.File]::WriteAllLines($diffPath, $report, [System.Text.UTF8Encoding]::new($false))
        Get-Content -LiteralPath $diffPath
        Write-Host "`nFull report: $diffPath"
        Write-Host "Candidate snapshot: $candidateDir"
    }
    'Promote' {
        if (-not (Test-Path -LiteralPath (Join-Path $candidateDir 'snapshot.json'))) {
            throw "No candidate exists at $candidateDir. Run -Mode Compare and review its report first."
        }
        if (Test-Path -LiteralPath $baselineDir) { Remove-Item -LiteralPath $baselineDir -Recurse -Force }
        Move-Item -LiteralPath $candidateDir -Destination $baselineDir
        Write-Host "Promoted the reviewed candidate to the local baseline: $baselineDir"
    }
}
