# KryoView

Prototipo Windows 11 x64 per pannelli TURZX/Turing da 8,8″. La finestra si chiude nell’area di notifica, dove **Esci** arresta l’app.

## Build

Esegui `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` dalla radice del progetto. Il risultato è `bin\KryoView.exe`. La build usa il compilatore di .NET Framework incluso in Windows. `KryoView.exe --self-test` verifica il pacchetto USB, l’enumerazione e la geometria dei fotogrammi.

## Prova sul pannello

1. Collega un TURZX 8,8″ USB V1.x e premi **Sensori**. Il menu di orientamento seleziona la skin orizzontale o verticale, anche capovolta. Il pannello mostra CPU, GPU, RAM, thread CPU, RAM usata e disponibile, spazio libero sul disco di sistema, traffico di rete in ingresso e in uscita, uptime e alimentazione. Le letture compaiono sul pannello. Il carico GPU usa il motore più impegnato tra quelli esposti dai contatori Windows. Una metrica indisponibile è indicata da un trattino.
2. Per la modalità monitor, usa **Installa driver monitor TURZX** dal menu dell’icona nell’area di notifica e indica l’INF originale del driver desktop. Estrai insieme tutti i file del pacchetto ufficiale, compreso il catalogo firmato. Windows chiede i privilegi per installarlo con PnPUtil. Il repository contiene il codice di integrazione. Il pacchetto si ottiene dal produttore.
3. Premi **Monitor USB**, poi **Schermo Windows** per impostare **Estendi** e l’orientamento del monitor.
4. Premi **Sensori** per tornare alla dashboard. KryoView invia il comando HID soltanto quando il dispositivo conferma il modello da 8,8″.

Il passaggio tra modalità e l’installazione con il pacchetto ufficiale richiedono un collaudo hardware. La Rev C può essere rilevata, mentre la dashboard e la modalità monitor sono implementate per la V1.x. La modalità monitor richiede il driver del produttore, mentre l’app TURZX può restare chiusa.

[Protocollo del pannello](https://github.com/jripper77/bezel-evo/blob/windows-improvements/docs/reverse-engineering/protocol-turing-usb.md#10-desktop-mode-and-the-hid-companion-1a86ad11) · [Gestione dei pacchetti con PnPUtil](https://learn.microsoft.com/windows-hardware/drivers/devtest/pnputil-command-syntax)
