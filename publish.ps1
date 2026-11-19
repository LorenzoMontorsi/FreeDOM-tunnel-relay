# Builds self-contained single-file binaries (no .NET runtime needed on the target).
#   dist/<rid>/freedom-server   server (CLI)
#   dist/<rid>/freedom-cli      client (CLI)
#   dist/<rid>/FreeDOM          client (GUI)   - on macOS also dist/<rid>/FreeDOM.app
param(
    [string[]]$Runtimes = @('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64'),
    [switch]$GuiOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Publish([string]$project, [string]$rid, [string[]]$extra) {
    dotnet publish "$root/src/$project" -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none @extra -o "$root/dist/$rid"
    if ($LASTEXITCODE -ne 0) { throw "publish of $project for $rid failed" }
    # Native packages (Skia, HarfBuzz) drop their own debug symbols next to the binary.
    Get-ChildItem "$root/dist/$rid" -Filter *.pdb | Remove-Item -Force
}

foreach ($rid in $Runtimes) {
    if (-not $GuiOnly) {
        Publish 'FreeDom.Server' $rid @()
        Publish 'FreeDom.Client' $rid @()
    }
    Publish 'FreeDom.Gui' $rid @('-p:IncludeNativeLibrariesForSelfExtract=true')

    if ($rid -like 'osx-*') {
        # Minimal .app bundle so it can live in /Applications and be launched from Finder.
        $app = "$root/dist/$rid/FreeDOM.app/Contents"
        New-Item -ItemType Directory -Force "$app/MacOS", "$app/Resources" | Out-Null
        Copy-Item "$root/dist/$rid/FreeDOM" "$app/MacOS/FreeDOM" -Force
        Copy-Item "$root/src/FreeDom.Gui/Assets/freedom.png" "$app/Resources/freedom.png" -Force
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>FreeDOM</string>
  <key>CFBundleDisplayName</key><string>FreeDOM</string>
  <key>CFBundleIdentifier</key><string>app.freedom.client</string>
  <key>CFBundleVersion</key><string>0.2.0</string>
  <key>CFBundleShortVersionString</key><string>0.2.0</string>
  <key>CFBundleExecutable</key><string>FreeDOM</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@ | Set-Content -Encoding utf8 "$app/Info.plist"
    }
}

Get-ChildItem "$root/dist" -Recurse -File |
    Where-Object { $_.FullName -notlike '*.app*' } |
    Select-Object @{ n = 'File'; e = { $_.FullName.Substring($root.Length + 1) } }, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
