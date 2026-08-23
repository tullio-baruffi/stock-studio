<#
.SYNOPSIS
    Crea o aggiorna 'metadata-generator-001': l'unico posto in cui vive il prompt dei metadati.

.DESCRIPTION
    La generazione dei metadati serviva due consumatori con esigenze diverse: la pipeline
    automatica (Logic App su coda) e l'applicazione (pulsante Rigenera, upload). Finora
    ognuno chiamava OpenAI per conto proprio con la propria copia del prompt, e le copie
    sono divergute.

    Questa Logic App e' un servizio sincrono: riceve un'immagine, la descrive e restituisce
    subito title/description/keywords. Entrambi i consumatori la chiamano, quindi il prompt
    esiste una volta sola.

    La direzione della dipendenza e' voluta. Una Logic App Consumption con trigger HTTP e'
    sempre pronta; l'API sta su un piano che si sospende. Far dipendere l'API dalla Logic App
    appoggia la parte fragile su quella robusta, non il contrario.

    Il contratto d'ingresso accetta sia un URL pubblico sia un data URL base64, perche' i due
    chiamanti hanno l'immagine in forme diverse: la pipeline ha il blob pubblico, l'API ha i
    byte scaricati da SharePoint.

.EXAMPLE
    .\metadata-generator.ps1            # mostra cosa farebbe
    .\metadata-generator.ps1 -Applica   # crea o aggiorna
#>
[CmdletBinding()]
param(
    [switch]$Applica,
    [string]$Name          = 'metadata-generator-001',
    [string]$ResourceGroup = 'rg-classifier',
    [string]$Subscription  = '3d72e432-e4d9-471d-9be5-2b0d1d8485bb',
    [string]$Location      = 'westeurope',
    [string]$Model         = 'gpt-5.6-luna',
    [string]$SourceWorkflow = 'resize-image-to-classify-001',
    [string]$PromptDir
)

$ErrorActionPreference = 'Stop'

if (-not $PromptDir) { $PromptDir = Join-Path $PSScriptRoot 'prompts' }
$systemText = (Get-Content (Join-Path $PromptDir 'metadata-system.txt') -Raw).TrimEnd()
$userText   = (Get-Content (Join-Path $PromptDir 'metadata-user.txt')   -Raw).TrimEnd()
# Qui il nome file arriva dal trigger, non da un messaggio in coda.
$userText   = $userText.Replace('{FILENAME}', "@{coalesce(triggerBody()?['fileName'], '')}")

function Wf($n) {
    "https://management.azure.com/subscriptions/$Subscription/resourceGroups/$ResourceGroup" +
    "/providers/Microsoft.Logic/workflows/$n`?api-version=2019-05-01"
}

# La chiave OpenAI non viene chiesta ne' stampata: si riusa quella gia' presente nel workflow
# esistente, cosi' resta un solo segreto da ruotare invece di due.
Write-Host "Lettura della credenziale OpenAI da '$SourceWorkflow'..." -ForegroundColor Cyan
$src = ((az rest --method get --uri (Wf $SourceWorkflow) --subscription $Subscription -o json) -join "`n") | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "Impossibile leggere '$SourceWorkflow'." }
$auth = $src.properties.definition.actions.HTTP.inputs.headers.Authorization
if (-not $auth) { throw "Header Authorization non trovato in '$SourceWorkflow'." }
Write-Host "  credenziale trovata ($($auth.Length) caratteri, non mostrata)" -ForegroundColor DarkGray

$definition = [ordered]@{
    '$schema'       = 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
    contentVersion  = '1.0.0.0'
    parameters      = @{}
    triggers        = [ordered]@{
        manual = [ordered]@{
            type = 'Request'
            kind = 'Http'
            inputs = [ordered]@{
                method = 'POST'
                schema = [ordered]@{
                    type = 'object'
                    properties = [ordered]@{
                        imageUrl = [ordered]@{ type = 'string' }
                        fileName = [ordered]@{ type = 'string' }
                    }
                    required = @('imageUrl')
                }
            }
        }
    }
    actions = [ordered]@{
        Genera = [ordered]@{
            type = 'Http'
            inputs = [ordered]@{
                method  = 'POST'
                uri     = 'https://api.openai.com/v1/chat/completions'
                headers = [ordered]@{
                    'Content-Type'  = 'application/json'
                    'Authorization' = $auth
                }
                body = [ordered]@{
                    model = $Model
                    messages = @(
                        [ordered]@{ role = 'system'; content = $systemText },
                        [ordered]@{
                            role = 'user'
                            content = @(
                                [ordered]@{ type = 'text'; text = $userText },
                                [ordered]@{ type = 'image_url'; image_url = [ordered]@{ url = "@{triggerBody()?['imageUrl']}" } }
                            )
                        }
                    )
                    # Lo schema fisso a valle non tollera JSON incorniciato in markdown.
                    response_format = [ordered]@{ type = 'json_object' }
                    # I modelli che ragionano consumano budget prima di scrivere: con un tetto
                    # basso la risposta torna vuota con finish_reason "length", e si paga lo stesso.
                    max_completion_tokens = 4000
                }
                retryPolicy = [ordered]@{ type = 'fixed'; count = 3; interval = 'PT10S' }
            }
        }
        Rispondi = [ordered]@{
            type = 'Response'
            kind = 'Http'
            runAfter = [ordered]@{ Genera = @('Succeeded') }
            inputs = [ordered]@{
                statusCode = 200
                headers = [ordered]@{ 'Content-Type' = 'application/json' }
                # Il contenuto arriva come stringa JSON: qui viene restituito come oggetto,
                # cosi' il chiamante non deve fare una seconda decodifica.
                body = "@json(body('Genera')?['choices'][0]?['message']?['content'])"
            }
        }
        RispondiErrore = [ordered]@{
            type = 'Response'
            kind = 'Http'
            # Senza questo ramo un errore di OpenAI lascerebbe il chiamante in attesa fino al
            # timeout, senza sapere cosa sia andato storto.
            runAfter = [ordered]@{ Genera = @('Failed', 'TimedOut') }
            inputs = [ordered]@{
                statusCode = 502
                headers = [ordered]@{ 'Content-Type' = 'application/json' }
                body = [ordered]@{
                    error  = 'Generazione dei metadati non riuscita.'
                    status = "@{outputs('Genera')?['statusCode']}"
                    detail = "@{outputs('Genera')?['body']}"
                }
            }
        }
    }
    outputs = @{}
}

$body = [ordered]@{
    location   = $Location
    properties = [ordered]@{ definition = $definition; parameters = @{}; state = 'Enabled' }
}

Write-Host ""
Write-Host "Logic App '$Name'" -ForegroundColor Cyan
Write-Host "  modello        : $Model"
Write-Host "  prompt system  : $($systemText.Length) caratteri"
Write-Host "  prompt user    : $($userText.Length) caratteri"
Write-Host "  ingresso       : { imageUrl (URL pubblico o data URL), fileName }"
Write-Host "  uscita         : { title, description, keywords }"

$exists = $false
az rest --method get --uri (Wf $Name) --subscription $Subscription -o none 2>$null
if ($LASTEXITCODE -eq 0) { $exists = $true }
Write-Host "  stato          : $(if ($exists) { 'esiste, verrebbe aggiornata' } else { 'da creare' })"

if (-not $Applica) {
    Write-Host ""
    Write-Host "  Esecuzione di sola lettura. Rilancia con -Applica per scrivere." -ForegroundColor DarkGray
    return
}

if ($exists) {
    $backup = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.backup'
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    $stamp = Get-Date -Format yyyyMMdd-HHmmss
    $prev = ((az rest --method get --uri (Wf $Name) --subscription $Subscription -o json) -join "`n")
    $prev | Set-Content (Join-Path $backup "logicapp-$Name-$stamp.json") -Encoding UTF8
    Write-Host "  backup salvato in .backup" -ForegroundColor DarkGray
}

$tmp = Join-Path $env:TEMP "metadata-generator-$(Get-Date -Format yyyyMMddHHmmss).json"
($body | ConvertTo-Json -Depth 60) | Set-Content $tmp -Encoding UTF8
try {
    az rest --method put --uri (Wf $Name) --subscription $Subscription --body "@$tmp" -o none
    if ($LASTEXITCODE -ne 0) { throw "Scrittura non riuscita." }
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

$after = ((az rest --method get --uri (Wf $Name) --subscription $Subscription -o json) -join "`n") | ConvertFrom-Json
$azioni = ($after.properties.definition.actions.PSObject.Properties | Measure-Object).Count
Write-Host ""
Write-Host ("  creata: stato {0}, {1} azioni" -f $after.properties.state, $azioni) -ForegroundColor Green

$cb = ((az rest --method post --subscription $Subscription --uri (
    "https://management.azure.com/subscriptions/$Subscription/resourceGroups/$ResourceGroup" +
    "/providers/Microsoft.Logic/workflows/$Name/triggers/manual/listCallbackUrl?api-version=2016-06-01") -o json) -join "`n") | ConvertFrom-Json
Write-Host ""
Write-Host "  URL di chiamata (contiene la firma: trattalo come un segreto):" -ForegroundColor Yellow
Write-Host "  $($cb.value)"
