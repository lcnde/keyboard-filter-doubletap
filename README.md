# Filtro doppio tap

Scarta i doppioni della tastiera (*key chatter*): quando lo switch "rimbalza" e la tastiera manda due pressioni per una battuta sola, il secondo carattere non arriva a nessun programma. Le doppie vere ("tutto", "allora", una freccia picchiettata) passano.

Fatto su misura per la mia Logitech G515 LIGHTSPEED TKL, con la soglia ricavata dai log del [Double Tap Detector](../double-tap-detector/). Come è fatto dentro: [CODICE.md](CODICE.md).

## Uso

1. Doppio clic su **`FiltroDoppioTap.exe`** (se è già attivo, ti avvisa e non ne apre un secondo).
2. Nell'area di notifica (in basso a destra, vicino all'orologio; se non la vedi, apri la freccia `^`) compare l'icona **"1x"**:
   - **verde**: filtro attivo;
   - **grigia**: filtro spento, perché l'hai messo in pausa o perché League of Legends è aperto.
3. Passando sopra l'icona vedi lo stato e quanti doppioni ha scartato da quando è partito.
4. Tasto destro sull'icona:
   - **Filtro attivo**: toglie o rimette la spunta (pausa / ripresa);
   - **Apri il log dei doppioni scartati**;
   - **Esci**.

## Avvio automatico

È **già attivo**: il filtro parte da solo ogni volta che accedi a Windows. Funziona con un collegamento nella cartella "Esecuzione automatica" del tuo utente (niente registro di sistema, niente permessi di amministratore).

### Attivarlo da solo

**Metodo 1, con lo script** (dalla cartella del filtro, in PowerShell o nel terminale):

```
powershell -ExecutionPolicy Bypass -File avvio-automatico.ps1
```

Crea il collegamento `Filtro doppio tap.lnk` nella cartella Esecuzione automatica.

**Metodo 2, a mano:**

1. Premi `Win + R`, scrivi `shell:startup` e premi Invio: si apre la cartella Esecuzione automatica.
2. In un'altra finestra apri la cartella del filtro.
3. Trascina `FiltroDoppioTap.exe` nella cartella Esecuzione automatica **tenendo premuto `Alt`**: così crei un collegamento invece di spostare il file.

### Disattivarlo

Una qualsiasi di queste:

- `powershell -ExecutionPolicy Bypass -File avvio-automatico.ps1 -Rimuovi`
- cancella `Filtro doppio tap.lnk` dalla cartella `shell:startup`;
- Gestione attività (`Ctrl + Maiusc + Esc`) → **App di avvio** → "Filtro doppio tap" → **Disabilita**.

### Se sposti la cartella

Il collegamento contiene il percorso della cartella: dopo averla spostata, rilancia `avvio-automatico.ps1` dalla nuova posizione.

## Come funziona

### Il problema

Quando premi un tasto, la tastiera manda al PC "premuto", poi "rilasciato". Se lo switch è usurato, durante una sola battuta il contatto si apre per un istante, e la tastiera manda "premuto, rilasciato, **premuto**, rilasciato": due caratteri per una battuta.

Sulla G515 la tastiera non trasmette mai pressioni o pause più corte di ~22 ms, quindi il doppione arriva così:

```
Doppione (1 battuta):   premuto 21 ms │ rilasciato 22 ms │ premuto 60 ms     seconda pressione dopo 43 ms
Doppia vera (2 battute): premuto 61 ms │ rilasciato 23 ms │ premuto 67 ms     seconda pressione dopo 84 ms
```

### La regola

Il filtro guarda il tempo **tra una pressione e la successiva dello stesso tasto**. Se la seconda arriva **entro 65 ms**, è un doppione: la scarta, insieme al suo rilascio e alle eventuali ripetizioni automatiche finché il tasto non viene lasciato. Il primo carattere resta, il secondo non arriva a nessun programma.

| Nei log del Double Tap Detector | Tempo tra le due pressioni | Filtro |
|---|---|---|
| Doppioni veri del tasto O | 43–56 ms | scartati |
| La mia doppia vera più veloce in assoluto | 69,9 ms | passa |
| Freccia picchiettata | 84–107 ms | passa |
| Doppie tipiche ("tt", "ll", "ss"…) | 100–200 ms | passano |

Rigiocando nel filtro tutti gli eventi registrati dal Double Tap Detector: **tutte le doppie vere passano** e **tutti i doppioni confermati della O vengono scartati** (vedi [Test](#test)).

**Perché non la pausa dal rilascio?** Sulla G515 la pausa vale ~22 ms sia nei doppioni sia quando ripremo un tasto molto in fretta (vedi "Il dito si alza o non si alza?" nel README del Double Tap Detector): guardando la pausa si scarterebbero anche lettere vere. È il motivo per cui il "Bounce Keys" di Windows, che misura dal rilascio, qui non va bene.

### Cosa non fa

- **Non genera mai input**: si limita a scartare. Non installa driver: usa solo l'hook di tastiera standard di Windows.
- **Non filtra Maiusc, Ctrl, Alt e Win**: si tengono premuti, e scartarne una ripressione li farebbe risultare rilasciati.
- **Non tocca il mouse**: i doppi click sono esclusi per costruzione.
- **Non salva il testo**: nel log finiscono solo i doppioni scartati (tasto e millisecondi).
- **Non tocca i tasti inviati dai programmi** (password manager, macro, tastiera su schermo).
- **Non vede le finestre aperte come amministratore** (limite di Windows per i programmi senza privilegi).
- **Non copre il raro "rimbalzo al rilascio"**: una pressione fantasma da ~22 ms subito dopo aver lasciato il tasto (vista una volta sulla barra spaziatrice, a 112 ms dalla pressione). Per riconoscerla bisognerebbe trattenere ogni pressione e reinviarla dopo, cioè generare input.

### League of Legends

Mentre il gioco è aperto (processo `League of Legends.exe`, controllato ogni secondo) il filtro **si spegne del tutto**: toglie il suo hook dalla tastiera e lo rimette quando chiudi il gioco. Durante una partita non c'è niente di suo agganciato alla tastiera; il rovescio è che in gioco, chat compresa, i doppioni non vengono filtrati.

Riot non pubblica cosa rileva Vanguard. Le sue regole ufficiali riguardano i programmi che danno un vantaggio misurabile, e i casi documentati di blocco riguardano strumenti che *generano* input finto, cosa che questo filtro non fa. Lo spegnimento durante il gioco elimina anche il dubbio residuo.

Per spegnerlo anche con altri giochi, aggiungi i nomi dei loro processi (senza `.exe`, separati da `;`) al collegamento di avvio: tasto destro su `Filtro doppio tap.lnk` → Proprietà → Destinazione, e in fondo aggiungi per esempio `--giochi "League of Legends;VALORANT-Win64-Shipping"`.

### Il log

`log\doppioni-scartati.csv` (cartella creata al primo doppione): data, ora, tasto e millisecondi dalla pressione precedente di ogni doppione scartato. Si apre con Excel; se è aperto in Excel mentre il filtro scrive, le righe vengono aggiunte appena lo chiudi.

Il filtro agisce **prima** del Double Tap Detector: i doppioni che scarta non arrivano nemmeno al rilevatore. Per contare i doppioni guarda questo log; il rilevatore continua a registrare quelli che passano (per esempio i rimbalzi al rilascio).

## Parametri

Si aggiungono dopo `FiltroDoppioTap.exe` (nel collegamento di avvio: Proprietà → Destinazione).

| Parametro | Predefinito | Significato |
|---|---|---|
| `--soglia 65` | 65 | ms massimi tra due pressioni dello stesso tasto per scartare la seconda |
| `--giochi "League of Legends"` | League of Legends | processi durante i quali il filtro si spegne, separati da `;` |

## Test

```
powershell -ExecutionPolicy Bypass -File test-logica.ps1
```

Prova la logica del filtro senza toccare la tastiera: 14 casi costruiti a mano più il replay di tutte le coppie registrate dal Double Tap Detector. Risultato al 12/10/2026: 653 doppie vere su 653 passano, 4 doppioni confermati della O su 4 scartati (più 96 righe successive, tutte passate).

Il programma vero è stato collaudato anche dal vivo, simulando un doppione con il tasto F24: con il filtro attivo agli altri programmi arriva una sola pressione, con il "gioco" aperto arrivano tutte. Dettagli in [CODICE.md](CODICE.md#come-è-stato-collaudato).

## File

| File | Cosa contiene |
|---|---|
| `FiltroDoppioTap.exe` | il programma |
| `FiltroDoppioTap.cs` | il codice sorgente (unico file) |
| `compila.cmd` | ricompila l'exe dal sorgente |
| `avvio-automatico.ps1` | attiva / disattiva l'avvio automatico |
| `test-logica.ps1` | test della logica |
| `CODICE.md` | come funziona il codice |
| `log\doppioni-scartati.csv` | i doppioni scartati |
