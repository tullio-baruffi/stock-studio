<#
.SYNOPSIS
    Porta il piano dell'API al livello indicato. App Service si paga a ore, quindi tenerlo su B1
    solo durante l'uso costa una frazione del canone mensile.

.DESCRIPTION
    F1 (gratuito) è il livello di riposo: l'API resta raggiungibile ma si sospende dopo 20 minuti
    di inattività, ha una quota di 60 minuti CPU al giorno e riparte a freddo in 15-30 secondi.
    B1 (~0,07 $/ora) toglie la quota, abilita Always On e fa sparire l'avvio a freddo.

    Il frontend non è coinvolto: è servito dallo storage statico, sempre acceso e a costo nullo.

.EXAMPLE
    .\api-plan.ps1 -Sku B1     # prima di una sessione di lavoro
    .\api-plan.ps1 -Sku F1     # a fine sessione
    .\api-plan.ps1             # mostra solo lo stato attuale e la spesa stimata
#>
[CmdletBinding()]
param(
    [ValidateSet('F1', 'B1', 'B2')]
    [string]$Sku,

    [string]$ResourceGroup = 'rg-classifier',
    [string]$Plan = 'asp-stock-api-001',
    [string]$Api = 'stock-vector-studio-api'
)

$ErrorActionPreference = 'Stop'

# Prezzi orari indicativi (West Europe, listino Windows). Servono solo a dare l'ordine di grandezza.
$costoOrario = @{ 'F1' = 0.0; 'B1' = 0.07; 'B2' = 0.15 }

function Stato {
    $s = az appservice plan show --name $Plan --resource-group $ResourceGroup --query "sku.name" -o tsv 2>$null
    if (-not $s) { throw "Piano '$Plan' non trovato nel gruppo '$ResourceGroup'." }
    return $s
}

$attuale = Stato
Write-Host "Piano API '$Plan': livello attuale $attuale" -ForegroundColor Cyan

# Su F1 il limite che conta davvero non e' l'avvio a freddo ma la quota CPU: esaurita, l'app
# risponde 403 per il resto della giornata. Vederla consumata dice se B1 serve davvero.
function QuotaCpu {
    $sub = az account show --query id -o tsv 2>$null
    if (-not $sub) { return $null }
    $uri = "https://management.azure.com/subscriptions/$sub/resourceGroups/$ResourceGroup" +
           "/providers/Microsoft.Web/sites/$Api/usages?api-version=2022-03-01"
    try {
        $u = az rest --method get --uri $uri -o json 2>$null | ConvertFrom-Json
        return $u.value | Where-Object { $_.name.value -eq 'CpuTime' } | Select-Object -First 1
    } catch { return $null }
}

$cpu = QuotaCpu
if ($cpu) {
    $usati = [math]::Round($cpu.currentValue / 1000, 0)
    if ($cpu.limit -gt 0) {
        $tot = [math]::Round($cpu.limit / 60000, 0)
        $pct = [math]::Round(100 * $cpu.currentValue / $cpu.limit, 1)
        Write-Host ("CPU consumata oggi: {0}s su {1} minuti ({2}%)" -f $usati, $tot, $pct) -ForegroundColor DarkGray
    } elseif ($attuale -eq 'F1') {
        # Subito dopo un cambio di livello Azure riporta limit = -1 per qualche minuto, ma la quota
        # dei 60 minuti su F1 esiste comunque: dirlo evita di leggere il dato come "illimitato".
        Write-Host ("CPU consumata oggi: {0}s (quota non ancora riesposta da Azure dopo il cambio di livello; su F1 restano 60 min al giorno)" -f $usati) -ForegroundColor DarkGray
    } else {
        Write-Host ("CPU consumata oggi: {0}s (nessun tetto su {1})" -f $usati, $attuale) -ForegroundColor DarkGray
    }
}

if (-not $Sku) {
    Write-Host ""
    Write-Host "  F1  gratuito, si sospende dopo 20 minuti di inattivita', 60 min CPU al giorno"
    Write-Host "  B1  circa 0,07 `$/ora (~1,70 `$/giorno se lasciato acceso), Always On, nessuna quota"
    Write-Host ""
    Write-Host "  Serve B1 solo se la quota CPU sopra si avvicina al 100%: l'avvio a freddo misurato"
    Write-Host "  su questa API e' sotto il secondo, quindi da solo non giustifica la spesa."
    Write-Host ""
    Write-Host "  Uso: .\api-plan.ps1 -Sku B1   prima di un lotto grosso"
    Write-Host "       .\api-plan.ps1 -Sku F1   quando hai finito"
    Write-Host ""
    Write-Host "  In ogni caso la Logic App 'api-plan-autoscaledown-001' riporta il piano su F1"
    Write-Host "  ogni sera alle 21, cosi' una dimenticanza non diventa un costo."
    return
}

if ($Sku -eq $attuale) {
    Write-Host "Gia' su ${Sku}: nulla da fare." -ForegroundColor Yellow
    return
}

Write-Host "Passaggio a $Sku..." -ForegroundColor Yellow

# Always On esiste solo dai piani Basic in su. Va spento PRIMA di scendere a F1, altrimenti il
# piano resterebbe con una configurazione che il livello gratuito non ammette.
if ($Sku -eq 'F1') {
    az webapp config set --name $Api --resource-group $ResourceGroup --always-on false -o none 2>$null
}

az appservice plan update --name $Plan --resource-group $ResourceGroup --sku $Sku -o none
if ($LASTEXITCODE -ne 0) { throw "Cambio di livello non riuscito." }

if ($Sku -ne 'F1') {
    az webapp config set --name $Api --resource-group $ResourceGroup --always-on true -o none 2>$null
    Write-Host "Always On attivato: niente piu' avvio a freddo." -ForegroundColor Green
}

$nuovo = Stato
Write-Host "Piano ora su $nuovo" -ForegroundColor Green

if ($costoOrario[$nuovo] -gt 0) {
    $h = $costoOrario[$nuovo]
    Write-Host ("Costo mentre resta acceso: ~{0:N2} `$/ora, ~{1:N2} `$ per una sessione di 2 ore." -f $h, ($h * 2)) -ForegroundColor DarkGray
    Write-Host "Ricordati di rimetterlo su F1 a fine lavoro: .\api-plan.ps1 -Sku F1" -ForegroundColor DarkGray
} else {
    Write-Host "Costo azzerato." -ForegroundColor DarkGray
}

# Un piano appena riportato a F1 puo' impiegare qualche secondo a rispondere.
Write-Host ""
Write-Host "Verifica dell'API..." -ForegroundColor Cyan
$up = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 3
    try {
        $r = Invoke-WebRequest "https://$Api.azurewebsites.net/api/health" -TimeoutSec 10 -ErrorAction Stop
        $up = $true; break
    } catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 401) { $up = $true; break }
    }
}
Write-Host $(if ($up) { "API raggiungibile." } else { "API non ancora raggiungibile: riprova fra poco." }) `
    -ForegroundColor $(if ($up) { 'Green' } else { 'Yellow' })
