[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectionString,

    [Parameter(Mandatory = $true)]
    [string]$Database,

    [Parameter(Mandatory = $true)]
    [string]$Container,

    [Parameter()]
    [string]$TargetContainer,

    [Parameter(Mandatory = $true)]
    [string[]]$PropertiesToTransform,

    [Parameter()]
    [int]$BatchSize = 100,

    [Parameter()]
    [int]$MaxDocuments = 0,

    [Parameter()]
    [switch]$BuildOnly,

    [Parameter()]
    [string]$ProjectPath = ''
)

$ErrorActionPreference = 'Stop'

try {
    if ([string]::IsNullOrWhiteSpace($TargetContainer)) {
        $TargetContainer = $Container
    }

    if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        $ProjectPath = Join-Path $PSScriptRoot '..\src\Solution2.DualPropertyPattern.csproj'
    }

    $resolvedProjectPath = [System.IO.Path]::GetFullPath($ProjectPath)

    if (-not (Test-Path -LiteralPath $resolvedProjectPath)) {
        throw "Project file not found: $resolvedProjectPath"
    }

    $installedSdks = & dotnet --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $installedSdks) {
        throw '.NET SDK 8.0 or later is required to build and run this tool. Install the SDK and rerun the script.'
    }

    Write-Host "Building dual-property migration tool..." -ForegroundColor Cyan
    & dotnet build $resolvedProjectPath --nologo --verbosity minimal

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    if ($BuildOnly) {
        Write-Host "BuildOnly specified. Compilation succeeded; no Cosmos DB changes were made." -ForegroundColor Yellow
        return
    }

    $propertyList = $PropertiesToTransform -join ','
    $arguments = @(
        'run',
        '--no-build',
        '--project', $resolvedProjectPath,
        '--',
        '--connection-string', $ConnectionString,
        '--database', $Database,
        '--source-container', $Container,
        '--target-container', $TargetContainer,
        '--properties', $propertyList,
        '--batch-size', $BatchSize
    )

    if ($MaxDocuments -gt 0) {
        $arguments += @('--max-documents', $MaxDocuments)
    }

    Write-Host "Running dual-property upsert job..." -ForegroundColor Cyan
    $output = & dotnet @arguments 2>&1
    $exitCode = $LASTEXITCODE

    $output | ForEach-Object { Write-Host $_ }

    if ($exitCode -ne 0) {
        throw "Dual-property tool exited with code $exitCode"
    }

    $summaryLine = $output | Where-Object { $_ -like 'SUMMARY_JSON:*' } | Select-Object -Last 1

    if ($summaryLine) {
        $summaryJson = $summaryLine.Substring('SUMMARY_JSON:'.Length)
        $summary = $summaryJson | ConvertFrom-Json

        Write-Host ''
        Write-Host 'Migration summary' -ForegroundColor Green
        Write-Host "  Documents read      : $($summary.documentsRead)"
        Write-Host "  Documents transformed: $($summary.documentsTransformed)"
        Write-Host "  Documents upserted  : $($summary.upsertedDocuments)"
        Write-Host "  Failed documents    : $($summary.failedDocuments)"
        Write-Host "  Properties added    : $($summary.propertiesAdded)"
        Write-Host "  Target container    : $($summary.targetContainer)"
        Write-Host "  Partition key path  : $($summary.partitionKeyPath)"
    }
    else {
        Write-Warning 'No structured summary was returned by the migration tool.'
    }
}
catch {
    Write-Error "apply-dual-properties.ps1 failed: $($_.Exception.Message)"
    exit 1
}
