# Sets the image variables only. Deploy from Coolify when ready to pull the images.
# Example: .\deploy\Set-CoolifyLatest.ps1
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$CoolifyUrl = 'https://coolify.zumbido.nl',

    [ValidatePattern('^[a-zA-Z0-9]+$')]
    [string]$ApplicationUuid = 'jk9geeq9jaaxlugouah40zyx',

    [ValidateNotNullOrEmpty()]
    [string]$Owner = 'rvlieshout',

    [System.Security.SecureString]$Token
)

$ErrorActionPreference = 'Stop'
$endpoint = '{0}/api/v1/applications/{1}/envs/bulk' -f $CoolifyUrl.TrimEnd('/'), $ApplicationUuid
$baseUri = [uri]$CoolifyUrl
if (-not $baseUri.IsAbsoluteUri -or $baseUri.Scheme -ne 'https' -or
    $baseUri.AbsolutePath -ne '/' -or $baseUri.Query -or $baseUri.Fragment -or $baseUri.UserInfo) {
    throw 'CoolifyUrl must be the HTTPS base URL, without an API path, query, or credentials.'
}

$images = @(
    @{ key = 'PLANNER_API_IMAGE'; value = "ghcr.io/$($Owner.ToLowerInvariant())/planner-api:latest" }
    @{ key = 'PLANNER_WEB_IMAGE'; value = "ghcr.io/$($Owner.ToLowerInvariant())/planner-web:latest" }
)
$body = @{ data = $images } | ConvertTo-Json -Depth 4

Write-Host "PATCH $endpoint"
foreach ($image in $images) {
    Write-Host "$($image.key)=$($image.value)"
}

if (-not $Token) {
    $Token = Read-Host 'Coolify API token (without the Bearer prefix)' -AsSecureString
}
if ($Token.Length -eq 0) {
    throw 'The API token must not be empty.'
}

$tokenPointer = [IntPtr]::Zero
$headers = @{}
try {
    $tokenPointer = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Token)
    $headers.Authorization = 'Bearer ' + [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
    $null = Invoke-RestMethod -Method Patch -Uri $endpoint -Headers $headers `
        -ContentType 'application/json' -Body $body -MaximumRedirection 0 -TimeoutSec 30
    Write-Host 'Both image variables are now set to :latest. No deployment was triggered.'
}
catch {
    $details = $_.ErrorDetails.Message
    if (-not $details) { $details = $_.Exception.Message }
    throw "Coolify update failed: $details"
}
finally {
    $headers.Clear()
    if ($tokenPointer -ne [IntPtr]::Zero) {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
    }
}
