param(
    [string]$SubscriptionId,
    [string]$ResourceGroupName,
    [string]$BaseName,
    [string[]]$Areas,
    [int]$DeleteAfterHours = 12,
    [switch]$Unique
)

. "$PSScriptRoot/../common/scripts/common.ps1"

$context = Get-AzContext
$account = $context.Account

function New-StringHash($string) {
    $hash = [System.Security.Cryptography.SHA1]::Create()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($string)
    $hashBytes = $hash.ComputeHash($bytes)
    return [BitConverter]::ToString($hashBytes) -replace '-', ''
}

$suffix = ($Unique ? [guid]::NewGuid().ToString() : (New-StringHash $account.Id)).ToLower().Substring(0, 8)

if(!$BaseName) {
    $BaseName = "mcp$($suffix)"
}

if(!$ResourceGroupName) {
    $username = $account.Id.Split('@')[0]
    $ResourceGroupName = "$username-mcp$($suffix)"
}

Push-Location $RepoRoot
try {
    $armParameters = @{ areas = ($Areas ?? @()) }

    Write-Host "Deploying:`n  ResourceGroupName: `"$ResourceGroupName`"`n  BaseName: `"$BaseName`"`n  DeleteAfterHours: $DeleteAfterHours`n  ArmTemplateParameters: $(ConvertTo-Json $armParameters -Compress)"

    if($SubscriptionId) {
        ./eng/common/TestResources/New-TestResources.ps1 `
            -SubscriptionId $SubscriptionId `
            -ResourceGroupName $ResourceGroupName `
            -BaseName $BaseName `
            -DeleteAfterHours $DeleteAfterHours `
            -AdditionalParameters $armParameters
    } else {
        ./eng/common/TestResources/New-TestResources.ps1 `
            -ResourceGroupName $ResourceGroupName `
            -BaseName $BaseName `
            -DeleteAfterHours $DeleteAfterHours `
            -AdditionalParameters $armParameters
    }
}
finally {
    Pop-Location
}
