<#
.SYNOPSIS
    Cambia l'intervallo di polling dei trigger delle Logic App della pipeline.

.DESCRIPTION
    Le Logic App consumption pagano ogni controllo del trigger, non il lavoro svolto: un connettore
    standard costa circa 0,000125 $ a controllo, quindi un polling al minuto vale ~4,6 EUR al mese
    anche quando non passa un solo file. Allungare l'intervallo taglia la spesa in proporzione
    esatta, al prezzo di un ritardo massimo pari al nuovo intervallo.

    Lo script lavora sulla definizione completa: legge il workflow, tocca solo recurrence del
    trigger e riscrive. Prima di ogni modifica salva un backup in .backup, e dopo rilegge dal
    servizio per confermare che il valore sia davvero cambiato e che il workflow sia ancora valido.

.EXAMPLE
    .\logicapp-polling.ps1                      # mostra intervalli e costo stimato
    .\logicapp-polling.ps1 -Applica             # applica il profilo equilibrato
#>
[CmdletBinding()]
param(
    [switch]$Applica,
    [string]$ResourceGroup = 'rg-classifier',
    [string]$Subscription  = '3d72e432-e4d9-471d-9be5-2b0d1d8485bb'
)

$ErrorActionPreference = 'Stop'

# Profilo equilibrato: la consegna SFTP e l'accodamento non hanno bisogno di reagire entro un
# minuto, mentre la generazione metadati resta la piu' reattiva perche' e' nel percorso in cui
# l'utente aspetta il risultato.
$obiettivi = [ordered]@{
    'invia-to-sftp'                = @{ frequency = 'Minute'; interval = 15; motivo = 'consegna finale, nessun bisogno di reagire al minuto' }
    'resize-image-to-classify-001' = @{ frequency = 'Minute'; interval = 3;  motivo = 'genera i metadati: e la piu reattiva delle tre' }
    'enqueu-image-la-001'          = @{ frequency = 'Minute'; interval = 15; motivo = 'accodamento, tollera ritardo' }
}

# Prezzo di un controllo per un connettore standard (listino Azure, USD).
$costoControllo = 0.000125

function Get-Workflow($nome) {
    $uri = "https://management.azure.com/subscriptions/$Subscription/resourceGroups/$ResourceGroup" +
           "/providers/Microsoft.Logic/workflows/$nome`?api-version=2019-05-01"
    $raw = (az rest --method get --subscription $Subscription --uri $uri -o json) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Lettura di '$nome' non riuscita." }
    return $raw | ConvertFrom-Json
}

function Get-Trigger($wf) {
    $t = $wf.properties.definition.triggers.PSObject.Properties | Select-Object -First 1
    if (-not $t) { throw "Nessun trigger trovato." }
    return $t
}

function CostoMensile($frequency, $interval) {
    if (-not $frequency -or -not $interval) { return $null }
    $alMese = switch ($frequency) {
        'Minute' { 43200 / $interval }
        'Hour'   { 720 / $interval }
        'Day'    { 30 / $interval }
        default  { return $null }
    }
    return [math]::Round($alMese * $costoControllo, 2)
}

Write-Host "Polling delle Logic App in '$ResourceGroup'" -ForegroundColor Cyan
Write-Host ""

$daFare = @()
foreach ($nome in $obiettivi.Keys) {
    $wf = Get-Workflow $nome
    $trg = Get-Trigger $wf
    $r = $trg.Value.recurrence
    $costoOra = CostoMensile $r.frequency $r.interval
    $costoDopo = CostoMensile $obiettivi[$nome].frequency $obiettivi[$nome].interval

    $uguale = ($r.frequency -eq $obiettivi[$nome].frequency -and $r.interval -eq $obiettivi[$nome].interval)
    $stato = if ($uguale) { 'gia a posto' } else { "da portare a $($obiettivi[$nome].interval) $($obiettivi[$nome].frequency)" }

    "  {0,-30} ogni {1,2} {2,-6} ~{3,5:N2} `$/mese  ->  {4}" -f $nome, $r.interval, $r.frequency, $costoOra, $stato
    "  {0,-30} {1}" -f '', $obiettivi[$nome].motivo

    if (-not $uguale) {
        $daFare += [pscustomobject]@{ Nome = $nome; Workflow = $wf; Trigger = $trg.Name; Prima = $costoOra; Dopo = $costoDopo }
    }
    Write-Host ""
}

if (-not $daFare) { Write-Host "Nulla da cambiare." -ForegroundColor Green; return }

$risparmio = ($daFare | Measure-Object -Property Prima -Sum).Sum - ($daFare | Measure-Object -Property Dopo -Sum).Sum
Write-Host ("Risparmio stimato: ~{0:N2} `$/mese" -f $risparmio) -ForegroundColor Yellow

if (-not $Applica) {
    Write-Host "Esecuzione di sola lettura. Rilancia con -Applica per scrivere." -ForegroundColor DarkGray
    return
}

$backup = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.backup'
New-Item -ItemType Directory -Force -Path $backup | Out-Null
$stamp = Get-Date -Format yyyyMMdd-HHmmss

foreach ($item in $daFare) {
    $nome = $item.Nome
    Write-Host "`n$nome" -ForegroundColor Cyan

    $wf = $item.Workflow
    ($wf | ConvertTo-Json -Depth 40) | Set-Content (Join-Path $backup "logicapp-$nome-$stamp.json") -Encoding UTF8

    $trg = $wf.properties.definition.triggers.$($item.Trigger)
    $trg.recurrence.frequency = $obiettivi[$nome].frequency
    $trg.recurrence.interval  = $obiettivi[$nome].interval

    # Il PUT vuole la definizione intera: si rimanda indietro quello che si e' letto, tenendo solo i
    # campi scrivibili. Passare le proprieta' di sola lettura (version, createdTime, accessEndpoint)
    # farebbe rifiutare la richiesta.
    $body = [ordered]@{
        location   = $wf.location
        properties = [ordered]@{
            definition = $wf.properties.definition
            parameters = $wf.properties.parameters
            state      = $wf.properties.state
        }
    }
    if ($wf.tags) { $body.tags = $wf.tags }
    if ($wf.identity) { $body.identity = $wf.identity }

    $tmp = Join-Path $env:TEMP "la-$nome-$stamp.json"
    ($body | ConvertTo-Json -Depth 40) | Set-Content $tmp -Encoding UTF8
    try {
        $uri = "https://management.azure.com/subscriptions/$Subscription/resourceGroups/$ResourceGroup" +
               "/providers/Microsoft.Logic/workflows/$nome`?api-version=2019-05-01"
        az rest --method put --uri $uri --subscription $Subscription --body "@$tmp" -o none
        if ($LASTEXITCODE -ne 0) { throw "Scrittura di '$nome' non riuscita: definizione invariata." }
    } finally {
        Remove-Item $tmp -ErrorAction SilentlyContinue
    }

    # Riletta dal servizio, non dalla variabile locale: e' l'unica prova che il valore sia passato.
    $dopo = Get-Workflow $nome
    $rd = (Get-Trigger $dopo).Value.recurrence
    $ok = ($rd.frequency -eq $obiettivi[$nome].frequency -and $rd.interval -eq $obiettivi[$nome].interval)
    $azioni = ($dopo.properties.definition.actions.PSObject.Properties | Measure-Object).Count

    if ($ok -and $dopo.properties.state -eq 'Enabled') {
        Write-Host ("  ora ogni {0} {1}, stato {2}, {3} azioni intatte" -f $rd.interval, $rd.frequency, $dopo.properties.state, $azioni) -ForegroundColor Green
    } else {
        Write-Host ("  ATTENZIONE: ogni {0} {1}, stato {2}" -f $rd.interval, $rd.frequency, $dopo.properties.state) -ForegroundColor Red
    }
}

Write-Host "`nFatto. I backup delle definizioni precedenti sono in $backup" -ForegroundColor Green
