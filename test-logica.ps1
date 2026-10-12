<#
    Test della logica del Filtro doppio tap (classe FiltroDoppioTap.Filtro dentro FiltroDoppioTap.exe).
    Non installa hook e non tocca la tastiera: passa al filtro sequenze di eventi con tempi simulati.

      powershell -ExecutionPolicy Bypass -File test-logica.ps1

    1. Casi costruiti a mano (doppioni veri della O, doppie vere piu' veloci, limite 64,9/65 ms, Maiusc, tasto tenuto...).
    2. Replay dei dati reali del Double Tap Detector (..\double-tap-detector\log\doppi-tap.csv), se presente:
       ogni coppia registrata viene rigiocata nel filtro. Fino al 12/10/2026 01:00 i dati sono classificati
       (doppioni confermati della O, prove fatte apposta, doppie vere) e verificati; le righe successive
       vengono solo contate.
#>
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFile((Join-Path $PSScriptRoot 'FiltroDoppioTap.exe'))
$falliti = 0

function Nuovo { $f = New-Object FiltroDoppioTap.Filtro; $f.SogliaMs = 65; $f }

# eventi: @(vk, 'giu'/'su', ms[, ms secondo l'orologio dei messaggi]); restituisce gli indici degli eventi scartati
function Gioca($f, $eventi) {
    $scartati = @()
    for ($i = 0; $i -lt $eventi.Count; $i++) {
        $e = $eventi[$i]
        $msg = if ($e.Count -ge 4) { [uint32]$e[3] } else { [uint32][math]::Round($e[2]) }
        $intervallo = 0.0
        if ($f.Decidi([int]$e[0], ($e[1] -eq 'giu'), [double]$e[2], $msg, [ref]$intervallo)) { $scartati += $i }
    }
    , $scartati
}

function Verifica($descrizione, $scartati, $attesi) {
    if (($scartati -join ',') -eq ($attesi -join ',')) { Write-Host "  OK      $descrizione" -ForegroundColor Green }
    else {
        Write-Host "  ERRORE  $descrizione (scartati: [$($scartati -join ',')], attesi: [$($attesi -join ',')])" -ForegroundColor Red
        $script:falliti++
    }
}

$O = 0x4F; $V = 0x56; $MAIUSC = 0xA0; $DESTRA = 0x27
Write-Host 'Casi costruiti a mano:'
Verifica 'Doppione della O (21,2 + 21,9 ms -> 43,1 ms): scartati pressione e rilascio' (Gioca (Nuovo) @(@($O,'giu',0), @($O,'su',21.2), @($O,'giu',43.1), @($O,'su',103.3))) @(2,3)
Verifica 'Doppione della O (34,1 + 21,8 ms -> 56,0 ms)' (Gioca (Nuovo) @(@($O,'giu',0), @($O,'su',34.1), @($O,'giu',56.0), @($O,'su',101.2))) @(2,3)
Verifica 'Doppia vera piu veloce registrata (69,9 ms): passa' (Gioca (Nuovo) @(@($O,'giu',0), @($O,'su',22.8), @($O,'giu',69.9), @($O,'su',92.9))) @()
Verifica 'Freccia picchiettata (61 + 23 ms -> 84 ms): passa' (Gioca (Nuovo) @(@($DESTRA,'giu',0), @($DESTRA,'su',61.1), @($DESTRA,'giu',84.0), @($DESTRA,'su',150.9))) @()
Verifica 'Doppia normale ("tt", 130 ms): passa' (Gioca (Nuovo) @(@($V,'giu',0), @($V,'su',70), @($V,'giu',130), @($V,'su',200))) @()
Verifica 'Esattamente 65 ms: passa' (Gioca (Nuovo) @(@($V,'giu',0), @($V,'su',30), @($V,'giu',65), @($V,'su',120))) @()
Verifica '64,9 ms: scartato' (Gioca (Nuovo) @(@($V,'giu',0), @($V,'su',30), @($V,'giu',64.9), @($V,'su',120))) @(2,3)
Verifica 'Maiusc ripremuto dopo 44 ms: mai filtrato' (Gioca (Nuovo) @(@($MAIUSC,'giu',0), @($MAIUSC,'su',22), @($MAIUSC,'giu',44), @($MAIUSC,'su',300))) @()
Verifica 'Tasto tenuto giu (ripetizione automatica): passa' (Gioca (Nuovo) @(@($V,'giu',0), @($V,'giu',500), @($V,'giu',533), @($V,'su',560))) @()
Verifica 'Doppione e poi tasto tenuto: scartate anche le ripetizioni fino al rilascio' (Gioca (Nuovo) @(@($O,'giu',0), @($O,'su',22), @($O,'giu',44), @($O,'giu',544), @($O,'giu',577), @($O,'su',600))) @(2,3,4,5)
Verifica 'Dopo un doppione, la pressione vera successiva passa' (Gioca (Nuovo) @(@($O,'giu',0), @($O,'su',22), @($O,'giu',44), @($O,'su',90), @($O,'giu',150), @($O,'su',230))) @(2,3)
Verifica 'Tasti diversi a 5 ms: passano' (Gioca (Nuovo) @(@($O,'giu',0), @($V,'giu',5), @($O,'su',60), @($V,'su',70))) @()
Verifica 'PC rallentato (40 ms per il timer, 120 ms per Windows): non scarta' (Gioca (Nuovo) @(@($O,'giu',0,0), @($O,'su',20,20), @($O,'giu',40,120), @($O,'su',90,170))) @()
Verifica 'Rilascio senza pressione (tasto gia giu all avvio): passa' (Gioca (Nuovo) @(@($O,'su',0), @($O,'giu',300), @($O,'su',380))) @()

$csv = Join-Path $PSScriptRoot '..\double-tap-detector\log\doppi-tap.csv'
if (Test-Path $csv) {
    Write-Host ''
    Write-Host 'Replay dei dati reali del Double Tap Detector:'
    $fs = [IO.File]::Open((Resolve-Path $csv), 'Open', 'Read', 'ReadWrite')
    $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8); $testo = $sr.ReadToEnd(); $sr.Close()
    $righe = @($testo -split "`r?`n" | Where-Object { $_ } | ConvertFrom-Csv)
    function N($v) { if ($v -eq '' -or $null -eq $v) { return -1 } return [double]$v }
    $conteggio = [ordered]@{}
    foreach ($r in $righe) {
        $p1 = N $r.'Prima pressione (ms)'; $pausa = N $r.'Pausa (ms)'; $p2 = N $r.'Seconda pressione (ms)'; $tra = N $r.'Tra le pressioni (ms)'
        if ($p1 -lt 0 -or $pausa -lt 0 -or $tra -lt 0) { continue }   # righe senza tempi completi
        if ($p2 -lt 0) { $p2 = 50 }
        $quando = "$($r.Data) $($r.Ora)"
        if ($quando -ge '2026-10-12 01:00') { $tipo = 'dopo il 12/10 01:00 (non classificata)' }
        elseif ($r.Tasto -eq 'O' -and $r.Data -eq '2026-10-12' -and $r.Esito -eq 'DOPPIO TAP') { $tipo = 'doppione confermato della O' }
        elseif (($quando -ge '2026-10-08 18:43' -and $quando -lt '2026-10-08 18:45:30') -or ($quando -ge '2026-10-09 14:29' -and $quando -lt '2026-10-09 14:31')) { $tipo = 'prova fatta apposta' }
        else { $tipo = 'doppia vera' }
        $vk = if ($r.Tasto.Length -eq 1) { [int][char]$r.Tasto.ToUpper() } else { 0x41 }
        $s = Gioca (Nuovo) @(@($vk,'giu',0), @($vk,'su',$p1), @($vk,'giu',$tra), @($vk,'su',$tra + $p2))
        $chiave = '{0} -> {1}' -f $tipo, $(if ($s.Count -gt 0) { 'scartata' } else { 'passa' })
        $conteggio[$chiave] = 1 + [int]$conteggio[$chiave]
    }
    $conteggio.GetEnumerator() | Sort-Object Name | ForEach-Object { '  {0,-55} {1}' -f $_.Name, $_.Value }
    if ([int]$conteggio['doppia vera -> scartata'] -ne 0) { Write-Host '  ERRORE  una doppia vera verrebbe scartata' -ForegroundColor Red; $falliti++ }
    else { Write-Host '  OK      nessuna doppia vera scartata' -ForegroundColor Green }
    if ([int]$conteggio['doppione confermato della O -> scartata'] -ne 4) { Write-Host '  ERRORE  non tutti i 4 doppioni della O vengono scartati' -ForegroundColor Red; $falliti++ }
    else { Write-Host '  OK      tutti e 4 i doppioni confermati della O scartati' -ForegroundColor Green }
}

Write-Host ''
if ($falliti -eq 0) { Write-Host 'TEST SUPERATI' -ForegroundColor Green } else { Write-Host "TEST FALLITI: $falliti" -ForegroundColor Red }
exit $falliti
