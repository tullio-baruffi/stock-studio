<#
.SYNOPSIS
    Aggiunge alla discesa serale del piano una guardia sul lavoro in corso.

.DESCRIPTION
    'api-plan-autoscaledown-001' riportava il piano a F1 ogni sera alle 21 guardando solo il
    livello attuale: non sapeva se in quel momento fosse in corso un lotto di immagini. Cambiare
    livello riavvia il sito, quindi una discesa a meta' di un lotto interrompeva il lavoro e
    rimetteva l'applicazione sotto la quota CPU del piano gratuito, con il rischio dei 403 per il
    resto della giornata.

    La guardia interroga le code della pipeline con l'identita' gestita del workflow: se anche una
    sola contiene messaggi, la discesa viene rimandata alla sera dopo. Una notte in piu' a
    pagamento costa meno di un lotto interrotto a meta'.

    Le code sono il segnale giusto perche' e' li' che vive il lavoro: vettorializzazione,
    classificazione e consegna passano tutte da un messaggio in coda.

.EXAMPLE
    .\logicapp-scaledown-guard.ps1            # mostra cosa cambierebbe
    .\logicapp-scaledown-guard.ps1 -Applica
#>
[CmdletBinding()]
param(
    [switch]$Applica,
    [string]$Workflow       = 'api-plan-autoscaledown-001',
    [string]$ResourceGroup  = 'rg-classifier',
    [string]$Subscription   = '3d72e432-e4d9-471d-9be5-2b0d1d8485bb',
    [string]$StorageAccount = 'rgclassifier8f3e',
    [string[]]$Queues = @('images-to-vectorize', 'image-to-classify', 'shrinked-image-to-classify', 'images-to-send')
)

$ErrorActionPreference = 'Stop'

$uri = "https://management.azure.com/subscriptions/$Subscription/resourceGroups/$ResourceGroup" +
       "/providers/Microsoft.Logic/workflows/$Workflow" + "?api-version=2019-05-01"

function Get-Workflow {
    $raw = (az rest --method get --uri $uri --subscription $Subscription -o json) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Lettura di '$Workflow' non riuscita." }
    return $raw | ConvertFrom-Json
}
function ActionName([string]$q) { 'Coda_' + ($q -replace '[^A-Za-z0-9]', '_') }

$wf  = Get-Workflow
$def = $wf.properties.definition

Write-Host "Logic App '$Workflow'" -ForegroundColor Cyan
Write-Host "  azioni attuali  : $((($def.actions.PSObject.Properties.Name) -join ', '))"
Write-Host "  code sorvegliate: $($Queues -join ', ')"
Write-Host "  guardia gia' presente: $($def.actions.PSObject.Properties.Name -contains (ActionName $Queues[0]))"

if (-not $Applica) {
    Write-Host ""
    Write-Host "  Aggiungerebbe un controllo sulle code prima di riportare il piano a F1." -ForegroundColor DarkGray
    Write-Host "  Rilancia con -Applica per scrivere." -ForegroundColor DarkGray
    return
}

$backup = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.backup'
New-Item -ItemType Directory -Force -Path $backup | Out-Null
$stamp = Get-Date -Format yyyyMMdd-HHmmss
($wf | ConvertTo-Json -Depth 60) | Set-Content (Join-Path $backup "logicapp-$Workflow-$stamp.json") -Encoding UTF8
Write-Host "  backup salvato in .backup" -ForegroundColor DarkGray

# Il trigger avvia in parallelo la lettura del piano e quella delle code.
# comp=metadata restituisce il conteggio in un header senza consumare messaggi: osservare non
# deve mai disturbare chi sta lavorando.
foreach ($q in $Queues) {
    $def.actions | Add-Member -NotePropertyName (ActionName $q) -NotePropertyValue ([ordered]@{
        type     = 'Http'
        runAfter = [ordered]@{}
        inputs   = [ordered]@{
            method  = 'GET'
            uri     = "https://$StorageAccount.queue.core.windows.net/$q" + "?comp=metadata"
            headers = [ordered]@{ 'x-ms-version' = '2021-08-06' }
            authentication = [ordered]@{ type = 'ManagedServiceIdentity'; audience = 'https://storage.azure.com/' }
        }
    }) -Force
}

# La discesa avviene solo se il piano non e' gia' F1 E tutte le code sono vuote.
# Una lettura fallita vale zero: un problema di osservazione non deve inchiodare il piano su B1.
$clauses = New-Object System.Collections.ArrayList
[void]$clauses.Add(@{ not = @{ equals = @("@body('LeggiPiano')?['sku']?['name']", 'F1') } })
foreach ($q in $Queues) {
    $a = ActionName $q
    [void]$clauses.Add(@{ equals = @(
        "@int(coalesce(outputs('$a')?['headers']?['x-ms-approximate-messages-count'], '0'))", 0) })
}

$cond = $def.actions.SeNonEGratuito
$cond.expression = @{ and = $clauses.ToArray() }

$runAfter = [ordered]@{ LeggiPiano = @('Succeeded') }
foreach ($q in $Queues) { $runAfter[(ActionName $q)] = @('Succeeded', 'Failed', 'TimedOut') }
$cond.runAfter = $runAfter

$body = [ordered]@{
    location   = $wf.location
    properties = [ordered]@{ definition = $def; parameters = $wf.properties.parameters; state = $wf.properties.state }
}
if ($wf.tags)     { $body.tags = $wf.tags }
if ($wf.identity) { $body.identity = $wf.identity }

$tmp = Join-Path $env:TEMP "guard-$stamp.json"
($body | ConvertTo-Json -Depth 60) | Set-Content $tmp -Encoding UTF8
try {
    az rest --method put --uri $uri --subscription $Subscription --body "@$tmp" -o none
    if ($LASTEXITCODE -ne 0) { throw "Scrittura non riuscita: definizione invariata." }
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

$after = Get-Workflow
$names = $after.properties.definition.actions.PSObject.Properties.Name
$ok = -not (($Queues | ForEach-Object { $names -contains (ActionName $_) }) -contains $false)
Write-Host ""
if ($ok -and $after.properties.state -eq 'Enabled') {
    Write-Host ("  guardia attiva: {0} code sorvegliate, stato {1}, {2} azioni" -f `
        $Queues.Count, $after.properties.state, $names.Count) -ForegroundColor Green
} else {
    Write-Host "  ATTENZIONE: verifica la definizione, backup in .backup" -ForegroundColor Red
}
