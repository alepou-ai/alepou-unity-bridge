param(
  [string]$ExpectedVersion = "0.18.0",
  [string]$PinnedInstallVersion = "0.18.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot "package.json"
$readmePath = Join-Path $repoRoot "README.md"
$bridgeWindowPath = Join-Path $repoRoot "Editor/AlepouUnityBridgeWindow.cs"

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.name -ne "com.alepou.unity-bridge") {
  throw "Unexpected package name: $($manifest.name)"
}
if ($manifest.displayName -ne "Alepou Unity Bridge") {
  throw "Unexpected display name: $($manifest.displayName)"
}
if ($manifest.version -ne $ExpectedVersion) {
  throw "package.json version $($manifest.version) does not match $ExpectedVersion"
}

$bridgeWindow = Get-Content -LiteralPath $bridgeWindowPath -Raw
$versionPattern = 'BridgeVersion\s*=\s*"' + [regex]::Escape($ExpectedVersion) + '"'
if ($bridgeWindow -notmatch $versionPattern) {
  throw "BridgeVersion constant does not match $ExpectedVersion"
}

$requiredPaths = @(
  "package.json",
  "README.md",
  "CHANGELOG.md",
  "LICENSE.md",
  "Editor/Alepou.UnityBridge.Editor.asmdef",
  "Editor/AlepouUnityBridgeService.cs",
  "Editor/AlepouUnityBridgeWindow.cs",
  "Editor/AlepouUnityBridgeWorkflowRunner.cs",
  "Editor/AlepouUnityBridgeTestRunner.cs",
  "Editor/AlepouUnityBridgeBuildRunner.cs"
)
foreach ($relativePath in $requiredPaths) {
  if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relativePath))) {
    throw "Required package file is missing: $relativePath"
  }
}

$assetFiles = Get-ChildItem -LiteralPath (Join-Path $repoRoot "Editor") -Recurse -File |
  Where-Object { $_.Extension -ne ".meta" }
foreach ($assetFile in $assetFiles) {
  if (-not (Test-Path -LiteralPath ($assetFile.FullName + ".meta"))) {
    throw "Unity asset has no .meta file: $($assetFile.FullName)"
  }
}

$guidOwners = @{}
Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter *.meta -File | ForEach-Object {
  $match = Select-String -LiteralPath $_.FullName -Pattern '^guid:\s*([0-9a-f]{32})\s*$' | Select-Object -First 1
  if (-not $match) { throw "Meta file has no valid GUID: $($_.FullName)" }
  $guid = $match.Matches[0].Groups[1].Value
  if ($guidOwners.ContainsKey($guid)) {
    throw "Duplicate Unity GUID $guid in $($_.FullName) and $($guidOwners[$guid])"
  }
  $guidOwners[$guid] = $_.FullName
}

$readme = Get-Content -LiteralPath $readmePath -Raw
$installUrl = "https://github.com/alepou-ai/alepou-unity-bridge.git#v$PinnedInstallVersion"
if (-not $readme.Contains($installUrl)) {
  throw "README does not contain the pinned install URL: $installUrl"
}
if ($readme -match 'github\.com/alepou-ai/alepou\.git\?path=') {
  throw "README still contains the retired monorepo package URL"
}
if ($readme -match '(?<!plan/unity/)recipes/(workflow-schema|structured-test-runs|structured-build-jobs|runtime-play-mode|deep-authoring)\.md') {
  throw "README contains a recipe path outside plan/unity/recipes"
}
foreach ($captureContractText in @(
  'target must be scene, game, both, or ui-builder',
  'Unity.UI.Builder.Builder',
  'CaptureEditorWindow',
  'open_ui_builder',
  'close_ui_builder',
  'frame_ui_builder_document',
  'FitViewport',
  'documentRootElement',
  'ResolveUiBuilderFrameTarget',
  'MaxUiBuilderCaptureEdge = 1920',
  'capturedAtSourceResolution',
  'downscaledAfterCapture',
  'Graphics.Blit',
  'capturesFullWindowBeforeDownscale',
  'hasUnsavedChanges',
  'requiresPostOpenEditorUpdate',
  'HasVisibleEditorPixels',
  'wait for one editor update',
  'AssetDatabase.OpenAsset',
  'VisualTreeAsset',
  'open_ui_builder assetPath must end with .uxml',
  'ui-builder-view.png',
  'ui-builder-view.json'
)) {
  if (-not $bridgeWindow.Contains($captureContractText)) {
    throw "UI Builder capture contract is missing: $captureContractText"
  }
}

Write-Output "Alepou Unity Bridge package validation passed."
Write-Output "Version: $ExpectedVersion"
Write-Output "Unity asset GUIDs: $($guidOwners.Count) unique"
Write-Output "Latest pinned release install URL: $installUrl"
