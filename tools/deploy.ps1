<#
.SYNOPSIS
    Compila frontend e API e li pubblica come una cosa sola sull'App Service dell'API.

.DESCRIPTION
    Prima il sito viveva su due indirizzi separati dall'API, e il bundle si portava dentro
    l'indirizzo dell'API (VITE_API_BASE) per poterla chiamare da un'altra origine. Quella
    separazione costava tre cose: la configurazione CORS, una chiave API condivisa incollata a mano
    in ogni browser, e due copie del frontend che potevano divergere.

    Qui la SPA viene messa nella wwwroot dell'API: stessa origine, quindi niente CORS, e soprattutto
    l'autenticazione di App Service (Easy Auth) puo' proteggere pagina e API con un solo cookie,
    senza che il frontend debba maneggiare token.

    Il prezzo e' l'indirizzo: si entra da stock-vector-studio-api.azurewebsites.net. Su piano F1 i
    domini personalizzati non sono ammessi, quindi non c'e' modo di renderlo piu' bello.

.NOTES
    La configurazione di Easy Auth non sta qui ma in tools\easy-auth-v2.json, che si applica cosi':

      $b = "/subscriptions/<sub>/resourceGroups/rg-classifier/providers/Microsoft.Web/sites/stock-vector-studio-api"
      az rest --method put --url "https://management.azure.com$b/config/authsettingsV2?api-version=2023-01-01" `
              --body "@tools\easy-auth-v2.json"

    Il file non contiene segreti: porta solo il nome dell'impostazione
    (MICROSOFT_PROVIDER_AUTHENTICATION_SECRET) il cui valore vive nelle app settings.
    Per spegnere l'autenticazione in caso di emergenza basta rimettere platform.enabled a false.

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -SoloFrontend
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'rg-classifier',
    [string]$WebApp = 'stock-vector-studio-api',
    [string]$Subscription = '3d72e432-e4d9-471d-9be5-2b0d1d8485bb',

    # Ricompila solo la SPA e la rimanda su, saltando la pubblicazione del backend.
    [switch]$SoloFrontend
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$frontend = Join-Path $root 'frontend'
$dist = Join-Path $frontend 'dist'
$apiProj = Join-Path $root 'backend\StockStudio.Api\StockStudio.Api.csproj'
$pub = Join-Path $env:TEMP 'svs-onehost-publish'

if (-not (Test-Path $frontend)) { throw "Cartella frontend non trovata: $frontend" }
if (-not (Test-Path $apiProj)) { throw "Progetto API non trovato: $apiProj" }

Write-Host 'Build del frontend (stessa origine dell.API)...' -ForegroundColor Cyan
Push-Location $frontend
try {
    # Deve restare vuota: con un indirizzo assoluto il bundle chiamerebbe un'altra origine, e le
    # richieste uscirebbero dal cookie di Easy Auth finendo tutte su una pagina di login.
    Remove-Item Env:\VITE_API_BASE -ErrorAction SilentlyContinue
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Build del frontend fallita.' }
} finally { Pop-Location }

$bundle = Get-ChildItem (Join-Path $dist 'assets') -Filter 'index-*.js' | Select-Object -First 1
if (-not $bundle) { throw 'Bundle non prodotto: build incompleta.' }

# Il controllo speculare a quello di prima: allora si pretendeva l'indirizzo dell'API dentro il
# bundle, adesso si pretende che non ci sia. Un residuo passerebbe inosservato fino al primo 302.
if (Select-String -Path $bundle.FullName -Pattern 'stock-vector-studio-api\.azurewebsites\.net' -Quiet) {
    throw "Il bundle contiene ancora un indirizzo assoluto dell'API: VITE_API_BASE non era vuota."
}
Write-Host "Bundle: $($bundle.Name) (stessa origine)" -ForegroundColor Green

if ($SoloFrontend) {
    if (-not (Test-Path (Join-Path $pub 'StockStudio.Api.dll'))) {
        throw "Nessuna pubblicazione precedente in $pub : esegui una volta senza -SoloFrontend."
    }
} else {
    Write-Host 'Pubblicazione del backend...' -ForegroundColor Cyan
    Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish $apiProj -c Release -o $pub --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Publish del backend fallita.' }
}

Write-Host 'Innesto della SPA nella wwwroot...' -ForegroundColor Cyan
$wwwroot = Join-Path $pub 'wwwroot'
Remove-Item $wwwroot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $wwwroot | Out-Null
Copy-Item (Join-Path $dist '*') $wwwroot -Recurse -Force

if (-not (Test-Path (Join-Path $wwwroot 'index.html'))) { throw 'index.html non e. finito nella wwwroot.' }

$zip = Join-Path $env:TEMP "svs-onehost-$(Get-Date -Format yyyyMMdd-HHmmss).zip"
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -Force
Write-Host ("Pacchetto: {0:N1} MB" -f ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green

try {
    Write-Host 'Deploy...' -ForegroundColor Cyan
    az webapp deploy -g $ResourceGroup -n $WebApp --subscription $Subscription --src-path $zip --type zip -o none
    if ($LASTEXITCODE -ne 0) { throw 'Deploy fallito.' }
} finally {
    Remove-Item $zip -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Verifica...' -ForegroundColor Cyan
$url = "https://$WebApp.azurewebsites.net/"
try {
    $r = Invoke-WebRequest $url -TimeoutSec 120 -UseBasicParsing -MaximumRedirection 0 -ErrorAction SilentlyContinue
    $code = [int]$r.StatusCode
} catch {
    $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    $r = $null
}

# Con Easy Auth acceso una richiesta anonima non torna 302: App Service risponde 200 con una
# paginetta che rimanda al login via JavaScript. Cercare il nome del bundle in quella pagina
# direbbe "vecchio" proprio quando tutto funziona, quindi si riconosce prima l'interstiziale.
$interstiziale = $r -and ($r.Content -match 'redirectToLoginPage' -or $r.Headers.Keys -contains 'x-ms-middleware-request-id')

if ($code -eq 302 -or $code -eq 401 -or $interstiziale) {
    Write-Host "  [ok]   $url richiede l.accesso: Easy Auth sta proteggendo il sito." -ForegroundColor Green
    Write-Host "         Il contenuto servito si verifica da browser, dopo l.autenticazione." -ForegroundColor DarkGray
} elseif ($r -and $r.Content -match [regex]::Escape($bundle.Name)) {
    Write-Host "  [ok]   $url serve $($bundle.Name)." -ForegroundColor Green
} elseif ($r) {
    Write-Host "  [vecchio] $url non cita $($bundle.Name) e non chiede l.accesso." -ForegroundColor Yellow
} else {
    Write-Host "  [errore] $url non ha risposto (codice $code)." -ForegroundColor Red
}
