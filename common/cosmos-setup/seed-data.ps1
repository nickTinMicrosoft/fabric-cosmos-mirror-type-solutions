[CmdletBinding()]
param(
    [Parameter()]
    [string]$DatabaseName = "FabricMirrorTypeDemo",

    [Parameter()]
    [string]$PartitionKeyPath = "/customerId",

    [Parameter()]
    [string]$Endpoint = "https://localhost:8081",

    [Parameter()]
    [string]$AccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",

    [Parameter()]
    [switch]$UseEmulator,

    [Parameter()]
    [switch]$AllowInsecureTlsForEmulator
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function New-CosmosDbAuthorizationHeader {
    param(
        [string]$Verb,
        [string]$ResourceType,
        [string]$ResourceLink,
        [string]$Date,
        [string]$MasterKey
    )

    $payload = "{0}`n{1}`n{2}`n{3}`n`n" -f $Verb.ToLowerInvariant(), $ResourceType.ToLowerInvariant(), $ResourceLink, $Date.ToLowerInvariant()
    $hmacSha256 = New-Object System.Security.Cryptography.HMACSHA256
    $hmacSha256.Key = [Convert]::FromBase64String($MasterKey)
    $hash = $hmacSha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))
    $signature = [Convert]::ToBase64String($hash)
    return [System.Web.HttpUtility]::UrlEncode("type=master&ver=1.0&sig=$signature")
}

function Invoke-CosmosDbRequest {
    param(
        [string]$Method,
        [string]$ResourceType,
        [string]$ResourceLink,
        [string]$Uri,
        [string]$AccountKey,
        [hashtable]$AdditionalHeaders,
        [object]$Body
    )

    $utcDate = [DateTime]::UtcNow.ToString("r")
    $authorization = New-CosmosDbAuthorizationHeader -Verb $Method -ResourceType $ResourceType -ResourceLink $ResourceLink -Date $utcDate -MasterKey $AccountKey

    $headers = @{
        "x-ms-date" = $utcDate
        "x-ms-version" = "2018-12-31"
        "Authorization" = $authorization
    }

    if ($AdditionalHeaders) {
        foreach ($key in $AdditionalHeaders.Keys) {
            $headers[$key] = $AdditionalHeaders[$key]
        }
    }

    $invokeParams = @{
        Method = $Method
        Uri = $Uri
        Headers = $headers
        ContentType = "application/json"
    }

    if ($null -ne $Body) {
        $invokeParams.Body = ($Body | ConvertTo-Json -Depth 100 -Compress)
    }

    Invoke-RestMethod @invokeParams
}

function Get-PartitionKeyValue {
    param(
        [pscustomobject]$Document,
        [string]$PartitionKeyPath
    )

    $propertyName = $PartitionKeyPath.TrimStart("/")
    $value = $Document.$propertyName
    if ($null -eq $value) {
        throw "Document '$($Document.id)' does not include partition key property '$propertyName'."
    }

    return $value
}

if ($UseEmulator -or $AllowInsecureTlsForEmulator) {
    Write-Step "Allowing emulator TLS certificate"
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
}

$sampleRoot = Split-Path -Parent $PSScriptRoot
$sampleDataRoot = Join-Path $sampleRoot "sample-data"

$containerMap = @(
    @{ Container = "orders-well-typed"; File = "well-typed-documents.json" },
    @{ Container = "orders-mixed-types"; File = "mixed-type-documents.json" },
    @{ Container = "orders-unsupported-types"; File = "unsupported-type-documents.json" }
)

foreach ($entry in $containerMap) {
    $filePath = Join-Path $sampleDataRoot $entry.File
    if (-not (Test-Path -LiteralPath $filePath)) {
        throw "Sample data file not found: $filePath"
    }

    Write-Step "Seeding container '$($entry.Container)' from '$($entry.File)'"
    $documents = Get-Content -LiteralPath $filePath -Raw | ConvertFrom-Json

    foreach ($document in $documents) {
        $resourceLink = "dbs/$DatabaseName/colls/$($entry.Container)"
        $uri = "$Endpoint/$resourceLink/docs"
        $partitionKeyValue = Get-PartitionKeyValue -Document $document -PartitionKeyPath $PartitionKeyPath
        $headers = @{
            "x-ms-documentdb-is-upsert" = "True"
            "x-ms-documentdb-partitionkey" = ((@($partitionKeyValue) | ConvertTo-Json -Compress))
        }

        $null = Invoke-CosmosDbRequest `
            -Method "POST" `
            -ResourceType "docs" `
            -ResourceLink $resourceLink `
            -Uri $uri `
            -AccountKey $AccountKey `
            -AdditionalHeaders $headers `
            -Body $document

        Write-Host "Upserted document $($document.id)" -ForegroundColor Green
    }
}

Write-Step "Seeding complete"
Write-Host "Database  : $DatabaseName"
Write-Host "Endpoint  : $Endpoint"
Write-Host "Containers: $($containerMap.Container -join ', ')"
Write-Host "Tip       : After loading data, configure Fabric mirroring against this database."
