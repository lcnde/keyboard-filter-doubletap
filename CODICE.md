# Come funziona il codice

Tutto il programma sta in un file, [`FiltroDoppioTap.cs`](FiltroDoppioTap.cs) (C# 5, .NET Framework 4.x, Windows Forms), compilato da `compila.cmd` con il compilatore C# già presente in Windows:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe ... FiltroDoppioTap.cs
```

`/target:winexe` produce un programma senza finestra di console: si vede solo l'icona nell'area di notifica.

## Il percorso di un tasto in Windows

```
tastiera ──► driver (kbdhid, kbdclass) ──► thread di input di Windows
                                              │
                                              ├─► 1. hook di tastiera a basso livello  ◄── qui lavora il filtro
                                              │      (se l'hook restituisce 1, l'evento si ferma qui)
                                              ├─► 2. Raw Input                          ◄── qui legge il Double Tap Detector
                                              └─► 3. coda dei messaggi ─► programma in primo piano
```

Un hook di tastiera a basso livello (`WH_KEYBOARD_LL`) è una funzione che Windows chiama per **ogni** pressione e rilascio, prima di consegnarli a chiunque altro. Se la funzione restituisce un valore diverso da zero, l'evento viene scartato: nessun programma lo riceve, e nemmeno il Raw Input (verificato nel collaudo dal vivo, vedi sotto).

## Le classi

Il file contiene cinque tipi, dal più interno al più esterno.

### `Filtro`: la decisione

Logica pura, senza chiamate a Windows: riceve un evento con i suoi tempi e risponde "scarta" o "lascia passare". Per questo si può testare con tempi simulati (`test-logica.ps1` carica l'exe e chiama direttamente questa classe).

Per ogni tasto (codice virtuale `vk`) tiene uno `Stato`:

| Campo | Significato |
|---|---|
| `Giu` | il tasto risulta premuto |
| `Scartando` | la pressione in corso è un doppione scartato |
| `HaAccettata` | c'è già stata almeno una pressione accettata |
| `UltimaMs` | istante dell'ultima pressione accettata (timer ad alta precisione) |
| `UltimaMsg` | lo stesso istante secondo l'orologio dei messaggi di Windows |

`Decidi(vk, giu, tMs, tMsg, out intervalloMs)` funziona così:

```
se è un RILASCIO:
    scarta se la pressione corrispondente era stata scartata   (pressione e rilascio del doppione spariscono insieme)
    Giu = false, Scartando = false

se è una PRESSIONE con il tasto già giù:                      (ripetizione automatica, tasto tenuto premuto)
    scarta se la pressione in corso è un doppione, altrimenti lascia passare

se è una PRESSIONE nuova:
    Giu = true
    se non è Maiusc/Ctrl/Alt/Win e c'è una pressione accettata precedente:
        d    = tMs  - UltimaMs                                (timer preciso)
        dMsg = tMsg - UltimaMsg                               (orologio dei messaggi, risoluzione ~16 ms)
        se d < 65 ms e dMsg <= 85 ms:  Scartando = true, scarta
    altrimenti accetta: UltimaMs = tMs, UltimaMsg = tMsg
```

Tre dettagli:

- **Il riferimento è l'ultima pressione *accettata***, non l'ultima ricevuta: se lo switch rimbalza due volte di fila, entrambe le ripressioni vengono misurate dalla pressione vera.
- **Il controllo con l'orologio dei messaggi** serve per i rallentamenti del PC: se Windows consegna all'hook due pressioni "ammucchiate" (vicine per il timer ma lontane per l'orologio dei messaggi, che segna l'ora in cui sono arrivate dalla tastiera), il filtro non scarta niente.
- **I modificatori sono esclusi** perché si tengono premuti: scartare una ripressione di Maiusc farebbe risultare il tasto rilasciato mentre lo tieni giù.

### `Scartato`: un doppione da registrare

Una struttura con ora, tasto e millisecondi. L'hook la mette in una coda (`ConcurrentQueue`) e torna subito a Windows; chi la scrive nel log è il thread dell'icona, un secondo dopo.

### `Gancio`: l'hook di tastiera

Installa e toglie l'hook su un **thread dedicato**, che non fa altro che aspettare messaggi (`GetMessage`). Due motivi:

- un hook a basso livello viene chiamato sul thread che l'ha installato, e quel thread deve elaborare messaggi;
- Windows dà all'hook un tempo massimo per rispondere: se il thread fosse occupato a disegnare l'icona o a scrivere il log, la tastiera rallenterebbe. Il thread dedicato ha priorità alta ed è sempre libero.

| Metodo | Cosa fa |
|---|---|
| `Avvia(attivo)` | crea il thread, la sua coda di messaggi e, se richiesto, installa l'hook |
| `Imposta(attivo)` | chiede di installare o togliere l'hook: manda al thread il messaggio `WM_APPLICA`, perché `SetWindowsHookEx` va chiamata dal thread che riceverà gli eventi |
| `Applica()` | sul thread dedicato: installa (`SetWindowsHookEx`) o toglie (`UnhookWindowsHookEx`) l'hook; quando lo reinstalla azzera gli stati, perché nel frattempo potrebbero essersi persi dei rilasci |
| `Callback(...)` | la funzione chiamata da Windows per ogni evento: legge la struttura `KBDLLHOOKSTRUCT`, ignora i tasti inviati dai programmi (flag `LLKHF_INJECTED`), chiede a `Filtro.Decidi` e restituisce `1` (scarta) oppure passa l'evento a `CallNextHookEx` |
| `Ferma()` | chiude il thread (`WM_QUIT`) e, uscendo, toglie l'hook |

Il delegato della callback è tenuto in un campo statico (`proc`): se il garbage collector lo eliminasse mentre Windows lo usa, il programma andrebbe in crash.

### `App`: l'icona e il controllo del gioco

Un `ApplicationContext` di Windows Forms senza finestre, solo con il `NotifyIcon`. Un timer scatta ogni secondo e chiama `Aggiorna()`:

1. `GiocoAperto()`: c'è un processo che si chiama come uno dei giochi (`Process.GetProcessesByName("League of Legends")`)?
2. Calcola se il filtro deve essere attivo (non in pausa e nessun gioco aperto) e lo comunica a `Gancio.Imposta`.
3. `RaccogliScartati()`: svuota la coda dei doppioni e li aggiunge a `log\doppioni-scartati.csv`. Se il file è aperto in Excel la scrittura fallisce e le righe restano in memoria fino al giro dopo.
4. Aggiorna icona (verde/grigia, disegnata a runtime da `DisegnaIcona`) e testo del tooltip.

Il separatore del CSV è quello delle impostazioni internazionali di Windows (`TextInfo.ListSeparator`), lo stesso che usa Excel; il file è UTF-8 con BOM.

### `Programma`: l'avvio

`Main` legge i parametri (`--soglia`, `--giochi`, più `--includi-simulati` ed `--esci-dopo`, che servono solo ai test), poi:

- un **mutex** con nome (`Local\FiltroDoppioTap`) impedisce di avere due filtri attivi: il secondo avvio mostra un messaggio ed esce;
- `Application.Run(new App(...))` resta in esecuzione finché scegli **Esci** dal menu;
- all'uscita `Gancio.Ferma()` toglie l'hook. Anche se il programma venisse chiuso a forza, Windows toglie da solo l'hook di un processo terminato.

## Scelte di progetto

| Scelta | Motivo |
|---|---|
| Soglia **da pressione a pressione**, 65 ms | Sulla G515 i doppioni arrivano a 43–56 ms e le doppie vere da ~70 ms in su; la pausa dal rilascio invece vale ~22 ms in entrambi i casi e non li distingue. |
| Si scartano pressione **e** rilascio del doppione | Così i programmi vedono una battuta normale, senza un rilascio "orfano". |
| **Mai generare input** | Niente tasti reinviati o ritardati: niente di simile a una macro, e nessun rischio di tasti inventati. Il prezzo è non poter filtrare il raro rimbalzo al rilascio. |
| Hook **tolto**, non solo ignorato, durante il gioco | Durante una partita il programma non ha niente agganciato alla tastiera. |
| Tasti dei programmi ignorati | Il filtro riguarda solo la tastiera fisica; password manager e macro passano intatti. |

## Come è stato collaudato

1. **Logica** (`test-logica.ps1`): 14 casi costruiti a mano più il replay di tutte le coppie registrate dal Double Tap Detector. 653 doppie vere su 653 passano, 4 doppioni confermati della O su 4 scartati.
2. **Dal vivo**: uno script di test installava un hook "osservatore" *prima* del filtro (Windows chiama per primo l'hook installato per ultimo, quindi l'osservatore vede solo ciò che il filtro lascia passare) e leggeva anche il Raw Input. Poi avviava `FiltroDoppioTap.exe --includi-simulati --esci-dopo 5` e inviava F24 (un tasto che nessun programma usa) giù/su/giù/su a ~15 ms di distanza:

| Caso | Arrivano ai programmi | Raw Input vede |
|---|---|---|
| Filtro attivo | 1 pressione + 1 rilascio | 1 pressione |
| "Gioco" aperto (`--giochi explorer`, un processo sempre presente) | 2 + 2 | 2 |
| Senza `--includi-simulati` (tasti dei programmi) | 2 + 2 | 2 |

## Modificare il programma

1. Modifica `FiltroDoppioTap.cs`.
2. Esci dal filtro (tasto destro sull'icona → Esci), altrimenti l'exe è in uso e non si può sovrascrivere.
3. Lancia `compila.cmd`.
4. Lancia `test-logica.ps1`.
5. Riavvia `FiltroDoppioTap.exe`.

Il codice deve restare compatibile con **C# 5**, l'ultima versione supportata dal compilatore incluso in Windows: niente `$"..."`, `?.`, `nameof`, proprietà con `=>`.
