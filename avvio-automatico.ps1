<#
    Attiva o disattiva l'avvio automatico del Filtro doppio tap all'accesso a Windows.
    Crea (o rimuove) un collegamento nella cartella "Esecuzione automatica" dell'utente:
    niente registro di sistema, niente privilegi di amministratore.

      powershell -ExecutionPolicy Bypass -File avvio-automatico.ps1            attiva
      powershell -ExecutionPolicy Bypass -File avvio-automatico.ps1 -Rimuovi   disattiva

    Il collegamento contiene il percorso attuale della cartella: se la sposti, rilancia questo script.
#>
param([switch]$Rimuovi)

$collegamento = Join-Path ([Environment]::GetFolderPath('Startup')) 'Filtro doppio tap.lnk'

if ($Rimuovi) {
    if (Test-Path $collegamento) { Remove-Item $collegamento }
    Write-Host 'Avvio automatico disattivato.'
    return
}

$exe = Join-Path $PSScriptRoot 'FiltroDoppioTap.exe'
if (-not (Test-Path $exe)) { Write-Host 'FiltroDoppioTap.exe non trovato: lancia prima compila.cmd.'; exit 1 }
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($collegamento)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $PSScriptRoot
$lnk.Description = 'Filtro doppio tap'
$lnk.Save()
Write-Host "Avvio automatico attivato: $collegamento"
