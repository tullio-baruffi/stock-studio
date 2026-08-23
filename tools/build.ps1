<#
.SYNOPSIS
    Compila Stock Vector Studio fermando prima il backend che blocca l'eseguibile.

.DESCRIPTION
    Su Windows un'app .NET in esecuzione tiene un lock su StockStudio.Api.exe, quindi
    "dotnet build" fallisce con MSB3021/MSB3027 mentre il sito e' avviato. Questo script
    ferma il backend, compila e - se richiesto - lo riavvia.

    Per sicurezza NON termina un processo qualsiasi in ascolto sulla porta: interrompe solo
    un processo il cui eseguibile si trova sotto la cartella del repository.

.EXAMPLE
    .\build.ps1                  # ferma il backend, compila, lo lascia spento
    .\build.ps1 -Restart         # ferma, compila e riavvia il backend
    .\build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [int]$Port = 5080,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Restart
)

$ErrorActionPreference = 'Stop'
$repo     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $repo 'StockVectorStudio.sln'
$apiDir   = Join-Path $repo 'stock-studio\backend\StockStudio.Api'
$wasRunning = $false

Write-Host "Repository: $repo" -ForegroundColor Cyan

# --- 1. libera il lock sull'eseguibile -------------------------------------------------
$conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($conn) {
    $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
    if ($null -eq $proc) {
        Write-Host "Porta $Port occupata da un processo non ispezionabile: procedo comunque." -ForegroundColor Yellow
    }
    elseif ($proc.Path -and $proc.Path.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Arresto backend: $($proc.ProcessName) (PID $($proc.Id))" -ForegroundColor Yellow
        Stop-Process -Id $proc.Id -Force
        # Il rilascio del lock non e' istantaneo dopo l'uscita del processo.
        for ($i = 0; $i -lt 20 -and -not $proc.HasExited; $i++) { Start-Sleep -Milliseconds 250 }
        Start-Sleep -Milliseconds 500
        $wasRunning = $true
    }
    else {
        throw "Sulla porta $Port c'e' '$($proc.ProcessName)' (PID $($proc.Id)), esterno al repository. Non lo tocco: chiudilo a mano o usa -Port."
    }
}
else {
    Write-Host "Nessun backend in ascolto sulla porta $Port." -ForegroundColor DarkGray
}

# --- 2. compila ------------------------------------------------------------------------
Write-Host "`nCompilazione ($Configuration)..." -ForegroundColor Cyan
& dotnet build $solution --configuration $Configuration --nologo
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Host "`nCompilazione FALLITA (exit $code)." -ForegroundColor Red
    exit $code
}
Write-Host "Compilazione riuscita." -ForegroundColor Green

# --- 3. riavvia se richiesto -----------------------------------------------------------
if ($Restart -or ($wasRunning -and $Restart)) {
    Write-Host "`nRiavvio del backend su http://127.0.0.1:$Port ..." -ForegroundColor Cyan
    $psi = "-NoProfile -Command `"`$env:ASPNETCORE_ENVIRONMENT='Development'; Set-Location '$apiDir'; dotnet run --no-build --configuration $Configuration --urls http://127.0.0.1:$Port`""
    Start-Process -FilePath 'powershell.exe' -ArgumentList $psi -WindowStyle Minimized | Out-Null

    $up = $false
    for ($i = 0; $i -lt 25; $i++) {
        Start-Sleep -Seconds 2
        try {
            Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 3 | Out-Null
            $up = $true; break
        } catch {
            # In locale non c'e' autenticazione davanti: quella di App Service vive solo in Azure,
            # quindi qui una risposta e' una risposta e un errore e' davvero un backend non pronto.
        }
    }
    if ($up) { Write-Host "Backend attivo su http://127.0.0.1:$Port" -ForegroundColor Green }
    else     { Write-Host "Backend non ha risposto entro 50s: controlla la finestra aperta." -ForegroundColor Red; exit 1 }
}
elseif ($wasRunning) {
    Write-Host "`nIl backend era attivo ed e' stato fermato. Riavvialo con -Restart." -ForegroundColor Yellow
}

Write-Host ""