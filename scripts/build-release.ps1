[CmdletBinding()]
param(
    [string]$OutputDir = "dist",
    [switch]$IncludePlugins,
    [switch]$ForceRebuildLmdb,
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"

# 1. Determine repository root
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root "PaperTodo.csproj") -PathType Leaf)) {
    $root = $PSScriptRoot
}
$root = [System.IO.Path]::GetFullPath($root)

if (-not (Test-Path (Join-Path $root "PaperTodo.csproj") -PathType Leaf)) {
    throw "Cannot locate PaperTodo.csproj at $root"
}

# 2. Check Git Submodule (vendor/wpf-notifyicon)
$submoduleCsproj = Join-Path $root "vendor\wpf-notifyicon\src\NotifyIconWpf\NotifyIconWpf.csproj"
if (-not (Test-Path -LiteralPath $submoduleCsproj -PathType Leaf)) {
    Write-Warning "Submodule vendor/wpf-notifyicon is missing or uninitialized. Initializing..."
    try {
        & git -C $root submodule update --init --recursive
    } catch {
        Write-Warning "Failed to execute git submodule update: $_"
    }
    if (-not (Test-Path -LiteralPath $submoduleCsproj -PathType Leaf)) {
        throw "vendor/wpf-notifyicon is required but missing. Please run 'git submodule update --init --recursive'."
    }
}

# 3. Detect .NET 10 SDK
$dotnetCmd = $null

# 3.1 Prefer dotnet in system PATH if it is version 10.x
$pathDotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
if ($pathDotnet) {
    try {
        $ver = & $pathDotnet.Source --version
        if ($ver -and $ver.StartsWith("10.")) {
            $dotnetCmd = $pathDotnet.Source
            Write-Host ">>> Using system .NET 10 SDK: $dotnetCmd ($ver)" -ForegroundColor Green
        }
    } catch { }
}

# 3.2 If system default is not 10.x, scan candidates
if (-not $dotnetCmd) {
    $candidates = [System.Collections.Generic.List[string]]::new()
    
    if ($env:DOTNET_ROOT) {
        $candidates.Add((Join-Path $env:DOTNET_ROOT "dotnet.exe"))
    }
    
    # Custom installation in user profile (~/app/dotnet-sdk-10.*/dotnet.exe)
    if ($env:USERPROFILE) {
        $customAppDir = Join-Path $env:USERPROFILE "app"
        if (Test-Path $customAppDir) {
            Get-ChildItem -Path $customAppDir -Filter "dotnet.exe" -Recurse -Depth 2 -ErrorAction SilentlyContinue |
                ForEach-Object { $candidates.Add($_.FullName) }
        }
    }
    
    # Standard installation paths
    $candidates.Add("C:\Program Files\dotnet\dotnet.exe")
    $candidates.Add("C:\Program Files (x86)\dotnet\dotnet.exe")

    foreach ($cand in $candidates) {
        if ($cand -and (Test-Path $cand -PathType Leaf)) {
            try {
                $ver = & $cand --version
                if ($ver -and $ver.StartsWith("10.")) {
                    $dotnetCmd = $cand
                    $env:DOTNET_ROOT = Split-Path -Parent $cand
                    $env:PATH = "$($env:DOTNET_ROOT);$($env:PATH)"
                    Write-Host ">>> Using detected .NET 10 SDK at: $cand ($ver)" -ForegroundColor Green
                    break
                }
            } catch { }
        }
    }
}

if (-not $dotnetCmd) {
    Write-Warning "Could not find a confirmed .NET 10 SDK. Falling back to default 'dotnet' command."
    $dotnetCmd = "dotnet"
}

# 4. Read project version
$projectXml = [xml](Get-Content -LiteralPath (Join-Path $root "PaperTodo.csproj") -Raw)
$versionNodes = $projectXml.SelectNodes('/Project/PropertyGroup/Version')
if ($versionNodes.Count -ne 1) {
    throw "PaperTodo.csproj must define exactly one <Version> element (found $($versionNodes.Count))."
}
$version = ([string]$versionNodes[0].InnerText).Trim()
if ([string]::IsNullOrWhiteSpace($version) -or $version -match '\s') {
    throw "PaperTodo.csproj <Version> is missing or contains whitespace: '$version'."
}
Write-Host ">>> PaperTodo Version: v$version" -ForegroundColor Cyan

# 5. Resolve and validate output directory safely
if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $distRoot = $OutputDir
} else {
    $distRoot = Join-Path $root $OutputDir
}
$distRoot = [System.IO.Path]::GetFullPath($distRoot)

# Guard against accidental deletion of root or drive roots
if ($distRoot.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir cannot be the repository root ($root)."
}
if ($root.StartsWith($distRoot, [System.StringComparison]::OrdinalIgnoreCase) -and $distRoot.Length -lt $root.Length) {
    throw "OutputDir cannot be an ancestor of repository root."
}
$distDriveRoot = [System.IO.Path]::GetPathRoot($distRoot)
if ($distRoot.TrimEnd('\', '/') -eq $distDriveRoot.TrimEnd('\', '/')) {
    throw "OutputDir cannot be a drive root: $distRoot"
}

$selfContainedOut = Join-Path $distRoot "tmp-self-contained"
$noRuntimeOut = Join-Path $distRoot "tmp-no-runtime"
$portableDir = Join-Path $distRoot "PaperTodo-v$version"

# Clean previous output directory
if (Test-Path $distRoot) {
    Write-Host ">>> Cleaning previous output directory: $distRoot" -ForegroundColor Yellow
    try {
        Remove-Item -LiteralPath $distRoot -Recurse -Force
    } catch {
        throw "Failed to clean output directory ($distRoot). Ensure no running PaperTodo processes or file locks exist: $_"
    }
}
New-Item -ItemType Directory -Force -Path $distRoot, $selfContainedOut, $noRuntimeOut, $portableDir | Out-Null

# 6. Ensure native LMDB library
$lmdbScript = Join-Path $root "native\lmdb\build.ps1"
$lmdbDll = Join-Path $root "native\lmdb\bin\win-x64\papertodo_lmdb.dll"
if ($ForceRebuildLmdb -or (-not (Test-Path $lmdbDll -PathType Leaf))) {
    Write-Host ">>> Preparing native LMDB (ForceRebuild=$ForceRebuildLmdb)..." -ForegroundColor Yellow
    $lmdbArgs = @{ Configuration = "Release" }
    if ($ForceRebuildLmdb) { $lmdbArgs["ForceRebuild"] = $true }
    & $lmdbScript @lmdbArgs
    if ($LASTEXITCODE -ne 0) { throw "Building native LMDB failed." }
}

try {
    # 7. Restore dependencies
    Write-Host ">>> Restoring dependencies..." -ForegroundColor Cyan
    & $dotnetCmd restore (Join-Path $root "PaperTodo.csproj")
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE" }

    # 8. Publish Self-Contained Single-File Executable
    Write-Host ">>> Publishing Self-Contained Single-File Executable..." -ForegroundColor Cyan
    & $dotnetCmd publish (Join-Path $root "PaperTodo.csproj") `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -o $selfContainedOut `
        /p:PublishSingleFile=true `
        /p:PublishReadyToRun=false `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=true `
        /p:PublishTrimmed=false `
        /p:EmbedAllSources=false `
        /p:EmbedUntrackedSources=false `
        /p:DebugType=none `
        /p:DebugSymbols=false

    if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed with exit code $LASTEXITCODE" }

    # 9. Publish Framework-Dependent (No-Runtime) Executable
    Write-Host ">>> Publishing Framework-Dependent (No-Runtime) Executable..." -ForegroundColor Cyan
    & $dotnetCmd publish (Join-Path $root "PaperTodo.csproj") `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -o $noRuntimeOut `
        /p:PublishSingleFile=true `
        /p:PublishReadyToRun=false `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=false `
        /p:PublishTrimmed=false `
        /p:EmbedAllSources=false `
        /p:EmbedUntrackedSources=false `
        /p:DebugType=none `
        /p:DebugSymbols=false

    if ($LASTEXITCODE -ne 0) { throw "Framework-dependent publish failed with exit code $LASTEXITCODE" }

    # 10. Assemble artifacts
    # 10.1 Standalone single-file executables for GitHub Release distribution
    $selfExeDist = Join-Path $distRoot "PaperTodo-v$version-win-x64-self-contained.exe"
    $noRuntimeExeDist = Join-Path $distRoot "PaperTodo-v$version-win-x64-no-runtime.exe"
    Copy-Item -LiteralPath (Join-Path $selfContainedOut "PaperTodo.exe") -Destination $selfExeDist -Force
    Copy-Item -LiteralPath (Join-Path $noRuntimeOut "PaperTodo.exe") -Destination $noRuntimeExeDist -Force

    # 10.2 Assemble portable package (only one default PaperTodo.exe)
    Write-Host ">>> Assembling Portable Package at: $portableDir" -ForegroundColor Cyan
    Copy-Item -LiteralPath (Join-Path $selfContainedOut "PaperTodo.exe") -Destination (Join-Path $portableDir "PaperTodo.exe") -Force

    # 10.3 Optional plugins bundling (per AGENTS.md, default release does not bundle plugins)
    if ($IncludePlugins) {
        Write-Host ">>> Building and bundling native plugins from repository..." -ForegroundColor Cyan
        $buildPluginScript = Join-Path $root "plugin-samples\Build-And-Install-NativePlugin.ps1"
        
        $pluginProjects = @(
            (Join-Path $root "plugin-samples\PaperTodo.Plugin.MinioSync\PaperTodo.Plugin.MinioSync.csproj"),
            (Join-Path $root "plugin-samples\PaperTodo.Plugin.LlmBridge\PaperTodo.Plugin.LlmBridge.csproj")
        )

        foreach ($proj in $pluginProjects) {
            if (Test-Path $proj -PathType Leaf) {
                Write-Host "    -> Building plugin: $(Split-Path -Leaf $proj)" -ForegroundColor DarkCyan
                & $buildPluginScript -ProjectPath $proj -PaperTodoRoot $root
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "Plugin build failed for $proj"
                }
            }
        }

        # Sync repository plugins directory to portableDir
        $repoPluginsDir = Join-Path $root "plugins"
        if (Test-Path $repoPluginsDir) {
            $portablePluginsDir = Join-Path $portableDir "plugins"
            New-Item -ItemType Directory -Force -Path $portablePluginsDir | Out-Null
            Copy-Item -Path (Join-Path $repoPluginsDir "*") -Destination $portablePluginsDir -Recurse -Force
        }
    }

    # 10.4 Compress portable ZIP package
    $zipPath = $null
    if (-not $SkipZip) {
        $zipPath = Join-Path $distRoot "PaperTodo-v$version-win-x64-portable.zip"
        Write-Host ">>> Compressing Portable ZIP: $zipPath..." -ForegroundColor Cyan
        Compress-Archive -Path "$portableDir\*" -DestinationPath $zipPath -Force
    }

    Write-Host "=== Build Completed Successfully! ===" -ForegroundColor Green
    Write-Host "Output Directory : $distRoot"
    Write-Host "  [1] Self-Contained Single-File : $selfExeDist"
    Write-Host "  [2] No-Runtime Single-File     : $noRuntimeExeDist"
    Write-Host "  [3] Portable Directory         : $portableDir"
    if ($zipPath) {
        Write-Host "  [4] Portable ZIP Archive       : $zipPath"
    }
}
finally {
    # Clean temporary directories
    Remove-Item -LiteralPath $selfContainedOut, $noRuntimeOut -Recurse -Force -ErrorAction SilentlyContinue
}
