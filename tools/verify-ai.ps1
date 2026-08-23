<#
.SYNOPSIS
    Verifica che il motore AI di Stock Vector Studio sia configurato e realmente raggiungibile.

.DESCRIPTION
    Quattro controlli in sequenza:
      1. /api/health            - il backend risponde
      2. /api/configuration     - le chiavi Ai:* risultano caricate (presenza, non valore)
      3. /api/trends/engine     - il router dichiara il motore agentico disponibile
      4. /api/trends/themes e /api/trends/relevance con refresh=true - andata e ritorno reale
         verso il modello. Sono gli unici endpoint agentici: espongono il campo "engine",
         che vale "agentic" solo se il modello ha risposto davvero.
         (/api/trends/live e /predicted sono deterministici per costruzione: leggono
         Google Trends, Wikipedia e Google News, quindi non provano nulla sull'AI.)

.EXAMPLE
    .\verify-ai.ps1
    .\verify-ai.ps1 -BaseUrl http://127.0.0.1:5080 -ApiKey "<Security:ApiKey se attiva>"
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5080',
    [string]$ApiKey
)

$ErrorActionPreference = 'Stop'
$headers = @{}
if ($ApiKey) { $headers['X-Api-Key'] = $ApiKey }
$script:agenticExpected = $false

function Step([string]$name, [scriptblock]$action) {
    Write-Host "`n== $name" -ForegroundColor Cyan
    try { & $action }
    catch {
        Write-Host "   FALLITO: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

Step '1. Backend raggiungibile' {
    $h = Invoke-RestMethod "$BaseUrl/api/health" -Headers $headers -TimeoutSec 10
    Write-Host "   backend=$($h.backend) jobStore=$($h.jobStore.ok) vectorizer=$($h.vectorizer.engine)" -ForegroundColor Gray
}

Step '2. Configurazione AI caricata' {
    $cfg = Invoke-RestMethod "$BaseUrl/api/configuration" -Headers $headers -TimeoutSec 15
    $ai = $cfg.groups | Where-Object { $_.key -eq 'ai' }
    Write-Host "   stato: $($ai.summary) [$($ai.state)]" -ForegroundColor Gray
    foreach ($i in $ai.items) { Write-Host ("   - {0,-28} {1}" -f $i.label, $i.value) -ForegroundColor DarkGray }
}

Step '3. Motore dichiarato dal router' {
    $e = Invoke-RestMethod "$BaseUrl/api/trends/engine" -Headers $headers -TimeoutSec 15
    $script:agenticExpected = [bool]$e.agentic
    Write-Host "   agentic=$($e.agentic) · $($e.label)" -ForegroundColor Gray
    if (-not $e.agentic) {
        Write-Host "   Nessun provider AI configurato: eseguire prima setup-ai.ps1 e riavviare il backend." -ForegroundColor Yellow
    }
}

Step '4. Andata e ritorno reale verso il modello' {
    Invoke-RestMethod "$BaseUrl/api/trends/cache/clear" -Method Post -Headers $headers -TimeoutSec 15 | Out-Null

    $checks = @(
        @{ Name = 'themes';    Url = "$BaseUrl/api/trends/themes?count=5&refresh=true" },
        @{ Name = 'relevance'; Url = "$BaseUrl/api/trends/relevance?topic=autumn%20leaves&refresh=true" }
    )

    $allAgentic = $true
    foreach ($c in $checks) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-RestMethod $c.Url -Headers $headers -TimeoutSec 180
        $sw.Stop()
        $isAgentic = ($r.engine -eq 'agentic')
        if (-not $isAgentic) { $allAgentic = $false }
        $color = if ($isAgentic) { 'Green' } else { 'Yellow' }
        Write-Host ("   {0,-10} engine={1,-14} {2,3}s  {3}" -f $c.Name, $r.engine, [int]$sw.Elapsed.TotalSeconds, $r.engineLabel) -ForegroundColor $color
        if ($r.warning) { Write-Host "     warning: $($r.warning)" -ForegroundColor Yellow }
    }

    if ($allAgentic) {
        Write-Host "   Motore agentico operativo: il modello ha risposto su entrambi gli endpoint." -ForegroundColor Green
    }
    elseif ($script:agenticExpected) {
        # Configurato ma non risponde: chiave, endpoint, deployment o rete.
        Write-Host "   ERRORE: provider configurato ma le risposte arrivano dal fallback deterministico." -ForegroundColor Red
        Write-Host "   Controllare chiave, endpoint/deployment e connettivita' verso il provider." -ForegroundColor Red
        exit 1
    }
    else {
        Write-Host "   Fallback deterministico, coerente con l'assenza di provider AI." -ForegroundColor Yellow
    }
}

Write-Host "`nVerifica completata.`n" -ForegroundColor Green