[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()]
    [string]$ResourceGroupName = "rg-fabric-cosmos-mirror-demo",

    [Parameter()]
    [string]$Location = "eastus",

    [Parameter()]
    [string]$AccountName = "cosmosfabricmirror$(Get-Random -Maximum 99999)",

    [Parameter()]
    [string]$DatabaseName = "FabricMirrorTypeDemo",

    [Parameter()]
    [string]$PartitionKeyPath = "/customerId",

    [Parameter()]
    [string]$EmulatorEndpoint = "https://localhost:8081",

    [Parameter()]
    [string]$EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",

    [Parameter()]
    [int]$EmulatorThroughput = 400,

    [Parameter()]
    [switch]$UseEmulator,

    [Parameter()]
    [switch]$SkipAccountCreation,

    [Parameter()]
    [switch]$CreateContainersOnly,

    [Parameter()]
    [int]$AutoscaleMaxThroughput = 4000
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

# Container names align with the sample-data folder.
# Keeping containers separate makes it easy to demonstrate clean, drifting,
# and unsupported-type scenarios side by side in Fabric.
$containers = @(
    @{ Name = "orders-well-typed"; PartitionKeyPath = $PartitionKeyPath },
    @{ Name = "orders-mixed-types"; PartitionKeyPath = $PartitionKeyPath },
    @{ Name = "orders-unsupported-types"; PartitionKeyPath = $PartitionKeyPath }
)

if ($UseEmulator) {
    Write-Step "Using Azure Cosmos DB Emulator"
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

    Write-Step "Ensuring emulator SQL database exists"
    try {
        $null = Invoke-CosmosDbRequest `
            -Method "POST" `
            -ResourceType "dbs" `
            -ResourceLink "" `
            -Uri "$EmulatorEndpoint/dbs" `
            -AccountKey $EmulatorKey `
            -AdditionalHeaders @{} `
            -Body @{ id = $DatabaseName }
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 409) {
            throw
        }
    }

    foreach ($container in $containers) {
        Write-Step "Ensuring emulator container '$($container.Name)' exists"
        $containerBody = @{
            id = $container.Name
            partitionKey = @{
                paths = @($container.PartitionKeyPath)
                kind = "Hash"
            }
        }

        try {
            $null = Invoke-CosmosDbRequest `
                -Method "POST" `
                -ResourceType "colls" `
                -ResourceLink "dbs/$DatabaseName" `
                -Uri "$EmulatorEndpoint/dbs/$DatabaseName/colls" `
                -AccountKey $EmulatorKey `
                -AdditionalHeaders @{ "x-ms-offer-throughput" = "$EmulatorThroughput" } `
                -Body $containerBody
        }
        catch {
            if ($_.Exception.Response.StatusCode.value__ -ne 409) {
                throw
            }
        }
    }

    Write-Step "Emulator provisioning complete"
    Write-Host "Endpoint   : $EmulatorEndpoint"
    Write-Host "Database   : $DatabaseName"
    Write-Host "Containers : $($containers.Name -join ', ')"
    Write-Host "Next step  : Run .\seed-data.ps1 -UseEmulator to load the sample documents."
    return
}

Write-Step "Checking Az.CosmosDB module"
if (-not (Get-Module -ListAvailable -Name Az.CosmosDB)) {
    throw "Az.CosmosDB is required. Install it with: Install-Module Az.CosmosDB -Scope CurrentUser"
}

Write-Step "Checking Azure context"
$context = Get-AzContext
if (-not $context) {
    throw "No Azure context found. Run Connect-AzAccount before running this script."
}

if (-not $CreateContainersOnly) {
    Write-Step "Ensuring resource group exists"
    $resourceGroup = Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue
    if (-not $resourceGroup) {
        if ($PSCmdlet.ShouldProcess($ResourceGroupName, "Create resource group")) {
            $null = New-AzResourceGroup -Name $ResourceGroupName -Location $Location
        }
    }
}

$account = $null
if (-not $SkipAccountCreation -and -not $CreateContainersOnly) {
    Write-Step "Ensuring Cosmos DB account exists with continuous backup"
    $account = Get-AzCosmosDBAccount -ResourceGroupName $ResourceGroupName -Name $AccountName -ErrorAction SilentlyContinue

    if (-not $account) {
        $locationObject = New-AzCosmosDBLocationObject -LocationName $Location -FailoverPriority 0

        if ($PSCmdlet.ShouldProcess($AccountName, "Create Azure Cosmos DB account")) {
            $account = New-AzCosmosDBAccount `
                -ResourceGroupName $ResourceGroupName `
                -Name $AccountName `
                -LocationObject $locationObject `
                -ApiKind Sql `
                -BackupPolicyType Continuous `
                -DefaultConsistencyLevel Session
        }
    }
    else {
        Write-Host "Cosmos DB account already exists: $($account.Name)" -ForegroundColor Green
        if ($account.BackupPolicyType -ne "Continuous") {
            Write-Warning "The existing account is not configured for continuous backup. Fabric mirroring requires continuous backup."
        }
    }
}
else {
    Write-Step "Using an existing Cosmos DB account"
    $account = Get-AzCosmosDBAccount -ResourceGroupName $ResourceGroupName -Name $AccountName -ErrorAction Stop
}

Write-Step "Ensuring SQL database exists"
$database = Get-AzCosmosDBSqlDatabase -ResourceGroupName $ResourceGroupName -AccountName $AccountName -Name $DatabaseName -ErrorAction SilentlyContinue
if (-not $database) {
    if ($PSCmdlet.ShouldProcess($DatabaseName, "Create Cosmos DB SQL database")) {
        $database = New-AzCosmosDBSqlDatabase `
            -ResourceGroupName $ResourceGroupName `
            -AccountName $AccountName `
            -Name $DatabaseName
    }
}

foreach ($container in $containers) {
    Write-Step "Ensuring container '$($container.Name)' exists"
    $existingContainer = Get-AzCosmosDBSqlContainer `
        -ResourceGroupName $ResourceGroupName `
        -AccountName $AccountName `
        -DatabaseName $DatabaseName `
        -Name $container.Name `
        -ErrorAction SilentlyContinue

    if (-not $existingContainer) {
        if ($PSCmdlet.ShouldProcess($container.Name, "Create Cosmos DB SQL container")) {
            $null = New-AzCosmosDBSqlContainer `
                -ResourceGroupName $ResourceGroupName `
                -AccountName $AccountName `
                -DatabaseName $DatabaseName `
                -Name $container.Name `
                -PartitionKeyKind Hash `
                -PartitionKeyPath $container.PartitionKeyPath `
                -AutoscaleMaxThroughput $AutoscaleMaxThroughput
        }
    }
    else {
        Write-Host "Container already exists: $($container.Name)" -ForegroundColor Green
    }
}

Write-Step "Provisioning complete"
Write-Host "Account name : $AccountName"
Write-Host "Database     : $DatabaseName"
Write-Host "Containers   : $($containers.Name -join ', ')"
Write-Host "Next step    : Run .\seed-data.ps1 to load the sample documents."
