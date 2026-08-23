<#
.SYNOPSIS
    Compila il frontend e lo pubblica su entrambe le destinazioni, cosi' non possono divergere.

.DESCRIPTION
    Il sito e' servito da due posti con caratteristiche diverse:
      - storage statico  https://rgclassifier8f3e.z6.web.core.windows.net  (sempre caldo, costo ~0)
      - App Service F1   https://stock-vector-studio.azurewebsites.net     (URL leggibile, si sospende)
    Sono la stessa applicazione. Pubblicarne una sola sarebbe l'errore facile da fare: chi apre
    l'altro indirizzo userebbe una versione vecchia senza accorgersene. Questo script fa un solo
    build e lo manda a tutte e due.

    L'indirizzo dell'API viene compilato dentro il bundle (VITE_API_BASE), perche' frontend e API
    stanno su origini diverse.

.EXAMPLE
    .\deploy-frontend.ps1
    .\deploy-frontend.ps1 -Solo blob
#>
[CmdletBinding()]
param(
    [ValidateSet('tutte', 'blob', 'appservice')]
    [string]$Solo = 'tutte',

    [string]$ApiBase = 'https://stock-vector-studio-api.azurewebsites.net',
    [string]$ResourceGroup = 'rg-classifier',
    [string]$StorageAccount = 'rgclassifier8f3e',
    [string]$WebApp = 'stock-vector-studio',
    [string]$Subscription = '3d72e432-e4d9-471d-9be5-2b0d1d8485bb'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$frontend = Join-Path $root 'frontend'
$dist = Join-Path $frontend 'dist'

if (-not (Test-Path $frontend)) { throw "Cartella frontend non trovata: $frontend" }

Write-Host "Build del frontend (API: $ApiBase)..." -ForegroundColor Cyan
Push-Location $frontend
try {
    $env:VITE_API_BASE = $ApiBase
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Build del frontend fallita." }
} finally {
    Remove-Item Env:\VITE_API_BASE -ErrorAction SilentlyContinue
    Pop-Location
}

$bundle = Get-ChildItem (Join-Path $dist 'assets') -Filter 'index-*.js' | Select-Object -First 1
if (-not $bundle) { throw "Bundle non prodotto: build incompleta." }

# Verifica che l'indirizzo dell'API sia finito davvero nel bundle: se VITE_API_BASE non viene letto
# il build riesce lo stesso, ma il sito chiamerebbe se stesso e ogni richiesta darebbe 404.
$host_ = ([Uri]$ApiBase).Host
if (-not (Select-String -Path $bundle.FullName -Pattern ([regex]::Escape($host_)) -Quiet)) {
    throw "Il bundle non contiene '$host_': VITE_API_BASE non e' stato applicato."
}
Write-Host "Bundle: $($bundle.Name) (API inclusa)" -ForegroundColor Green

if ($Solo -in @('tutte', 'blob')) {
    Write-Host "Pubblicazione sullo storage statico..." -ForegroundColor Cyan
    $key = az storage account keys list -g $ResourceGroup -n $StorageAccount --subscription $Subscription `
        --query "[0].value" -o tsv
    if (-not $key) { throw "Chiave dello storage non recuperata." }

    az storage blob upload-batch --account-name $StorageAccount --account-key $key `
        --destination '$web' --source $dist --overwrite -o none
    if ($LASTEXITCODE -ne 0) { throw "Upload sullo storage fallito." }

    # upload-batch deduce gia' il content-type dall'estensione, ma un MIME sbagliato romperebbe il
    # sito in modo silenzioso (il browser rifiuta un modulo servito come octet-stream), quindi lo
    # si verifica e si corregge solo se serve.
    # Nota: i --query di JMESPath con [? ] passano da cmd e vengono spezzati, quindi si filtra qui.
    $attesi = @{ '.js' = 'application/javascript'; '.css' = 'text/css'; '.html' = 'text/html'; '.svg' = 'image/svg+xml' }
    $blobs = az storage blob list --account-name $StorageAccount --account-key $key -c '$web' -o json | ConvertFrom-Json
    foreach ($b in $blobs) {
        $ext = [IO.Path]::GetExtension($b.name).ToLowerInvariant()
        if (-not $attesi.ContainsKey($ext)) { continue }
        if ($b.properties.contentSettings.contentType -ne $attesi[$ext]) {
            Write-Host "  correggo il MIME di $($b.name)" -ForegroundColor Yellow
            az storage blob update --account-name $StorageAccount --account-key $key -c '$web' `
                -n $b.name --content-type $attesi[$ext] -o none
        }
    }
    Write-Host "Storage statico aggiornato." -ForegroundColor Green
}

if ($Solo -in @('tutte', 'appservice')) {
    Write-Host "Pubblicazione sull'App Service..." -ForegroundColor Cyan

    # L'App Service ha bisogno del web.config: senza, le rotte del client danno 404 al refresh.
    $webConfig = Join-Path $dist 'web.config'
    if (-not (Test-Path $webConfig)) {
        $src = Join-Path $PSScriptRoot 'frontend-web.config'
        if (Test-Path $src) { Copy-Item $src $webConfig }
        else { Write-Warning "frontend-web.config non trovato: le rotte del client potrebbero dare 404." }
    }

    $zip = Join-Path $env:TEMP "frontend-$(Get-Date -Format yyyyMMdd-HHmmss).zip"
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -Force
    try {
        az webapp deploy -g $ResourceGroup -n $WebApp --subscription $Subscription `
            --src-path $zip --type zip -o none
        if ($LASTEXITCODE -ne 0) { throw "Deploy sull'App Service fallito." }
    } finally {
        Remove-Item $zip -ErrorAction SilentlyContinue
    }
    Write-Host "App Service aggiornato." -ForegroundColor Green
}

Write-Host ""
Write-Host "Verifica..." -ForegroundColor Cyan
$bersagli = @()
if ($Solo -in @('tutte', 'blob')) { $bersagli += "https://$StorageAccount.z6.web.core.windows.net/" }
if ($Solo -in @('tutte', 'appservice')) { $bersagli += "https://$WebApp.azurewebsites.net/" }

foreach ($u in $bersagli) {
    try {
        $html = (Invoke-WebRequest $u -TimeoutSec 90 -UseBasicParsing).Content
        $ok = $html -match [regex]::Escape($bundle.Name)
        Write-Host ("  {0} {1}" -f $(if ($ok) { '[ok]  ' } else { '[vecchio]' }), $u) `
            -ForegroundColor $(if ($ok) { 'Green' } else { 'Yellow' })
    } catch {
        Write-Host "  [errore] $u -> $($_.Exception.Message)" -ForegroundColor Red
    }
}
