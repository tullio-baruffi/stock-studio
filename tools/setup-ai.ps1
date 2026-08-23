<#
.SYNOPSIS
    Configura le credenziali del motore AI di Stock Vector Studio usando dotnet user-secrets.

.DESCRIPTION
    I secret NON finiscono mai nei file del progetto: restano nel profilo utente
    (%APPDATA%\Microsoft\UserSecrets). In produzione usa invece Azure Key Vault
    (KeyVault:Uri) o variabili ambiente con doppio underscore (es. Ai__ApiKey).

.EXAMPLE
    .\setup-ai.ps1 -Provider azure -Endpoint https://mia-risorsa.openai.azure.com -Deployment gpt-4o -ApiKey "<chiave>"

.EXAMPLE
    .\setup-ai.ps1 -Provider openai -ApiKey "sk-..." -Model gpt-4o-mini

.EXAMPLE
    .\setup-ai.ps1 -Provider compatible -Endpoint http://localhost:11434 -Model qwen2.5:14b

.EXAMPLE
    .\setup-ai.ps1 -Provider stub          # torna al motore deterministico e pulisce le chiavi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('azure', 'openai', 'compatible', 'stub')]
    [string]$Provider,

    # Omettila per digitarla in un prompt nascosto: cosi' non finisce nella cronologia
    # di PowerShell (PSReadLine) ne' nella riga di comando visibile agli altri processi.
    [string]$ApiKey,
    [string]$Endpoint,
    [string]$Deployment,
    [string]$Model,
    [string]$ApiVersion
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\backend\StockStudio.Api\StockStudio.Api.csproj' | Resolve-Path

function Set-Secret([string]$key, [string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return }
    dotnet user-secrets --project $project set $key $value | Out-Null
    $shown = if ($key -like '*ApiKey*') { '***' } else { $value }
    Write-Host ("  {0,-16} = {1}" -f $key, $shown) -ForegroundColor DarkGray
}

function Remove-Secret([string]$key) {
    dotnet user-secrets --project $project remove $key 2>$null | Out-Null
}

function Read-ApiKeySecurely {
    $secure = Read-Host -Prompt '  Incolla la API key (non verra'' visualizzata)' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# Validazione dei requisiti per provider: meglio fallire qui che a runtime.
switch ($Provider) {
    'azure' {
        if (-not $Endpoint)   { throw "Con -Provider azure serve -Endpoint (es. https://mia-risorsa.openai.azure.com)." }
        if (-not $Deployment) { throw "Con -Provider azure serve -Deployment (il nome del deployment, non del modello)." }
        if (-not $ApiKey)     { $ApiKey = Read-ApiKeySecurely }
        if (-not $ApiKey)     { throw "API key non fornita." }
    }
    'openai' {
        if (-not $ApiKey) { $ApiKey = Read-ApiKeySecurely }
        if (-not $ApiKey) { throw "API key non fornita." }
        if (-not $ApiKey.StartsWith('sk-')) {
            Write-Warning "La chiave non inizia con 'sk-': controlla di non aver incollato un ID progetto/organizzazione."
        }
        if (-not $Model)  { $Model = 'gpt-4o-mini' }
    }
    'compatible' {
        if (-not $Endpoint) { throw "Con -Provider compatible serve -Endpoint (es. http://localhost:11434)." }
        if (-not $Model)    { throw "Con -Provider compatible serve -Model." }
    }
}

Write-Host "`nProgetto: $project" -ForegroundColor Cyan
dotnet user-secrets --project $project init | Out-Null

if ($Provider -eq 'stub') {
    Write-Host "Ripristino del motore deterministico..." -ForegroundColor Yellow
    'Ai:ApiKey', 'Ai:Endpoint', 'Ai:Deployment', 'Ai:Model', 'Ai:ApiVersion' | ForEach-Object { Remove-Secret $_ }
    Set-Secret 'Ai:Provider' 'stub'
} else {
    Write-Host "Configurazione provider '$Provider'..." -ForegroundColor Yellow
    if ($Provider -eq 'openai') {
        # OpenAI usa l'URL pubblico e il nome modello: chiavi residue di un provider
        # precedente renderebbero ambigua la diagnostica della pagina Configurazione.
        'Ai:Endpoint', 'Ai:Deployment' | ForEach-Object { Remove-Secret $_ }
    }
    Set-Secret 'Ai:Provider'   $Provider
    Set-Secret 'Ai:ApiKey'     $ApiKey
    Set-Secret 'Ai:Endpoint'   $Endpoint
    Set-Secret 'Ai:Deployment' $Deployment
    Set-Secret 'Ai:Model'      $Model
    Set-Secret 'Ai:ApiVersion' $ApiVersion
}

Write-Host "`nFatto. Riavvia il backend, poi verifica con:" -ForegroundColor Green
Write-Host "  .\verify-ai.ps1`n" -ForegroundColor Green