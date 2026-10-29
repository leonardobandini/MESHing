# MESHing 3.0.3 — rilascio stabile

Questa versione è destinata alla distribuzione pubblica, non è una build di
testing. Binario, sorgenti corrispondenti, licenze e checksum sono asset della
stessa release `v3.0.3`; l'identità COM rimane invariata per aggiornare le
installazioni esistenti.

## Correzioni

- Alla riapertura del plugin si conservano soltanto dimensioni massima/minima
  e ultima lingua selezionata; le altre opzioni tornano ai default. Senza
  preferenza salvata, viene usata la lingua dell'interfaccia di sistema.
- Il testo italiano di FAST nelle opzioni di trasferimento non è tagliato.
- Viene rilevato un cambio delle impostazioni internazionali di Windows
  successivo all'avvio **dell'effettivo processo ospite** SAP2000 o CSiBridge,
  anche se avvenuto prima di aprire MESHing. Il messaggio cita soltanto l'host
  corrente e invita a chiudere il modello senza salvare, uscire dall'host e
  riavviarlo. Durante la meshatura, un cambio di formato impedisce Applica,
  Annulla o il rollback delle tabelle in un formato incoerente: in quel caso
  occorre riaprire il modello salvato prima della generazione, controllando
  il backup numerato se il cambio è avvenuto durante il salvataggio pre-mesh.

## Build e verifiche

- Plugin, installer CLI e installer grafico `net48` x64; backend Python 3.12.7
  con Gmsh 4.15.2. Confronto SHA-256 delle licenze upstream, delle sorgenti
  nativi e del binding Gmsh: controlli nel build `packaging/build-release-v2.ps1`.
- I test unitari/UI riguardano impostazioni, localizzazione, avviso dipendente
  dall'host e data/ora di modifica del registro in UTC; gli smoke test includono
  Undo FAST su SAP2000 e CSiBridge con modelli isolati.
- Matrice definitiva: 36 casi Apply + 36 casi Undo su SAP2000, altrettanti su
  CSiBridge; 3 sessioni persistenti in parallelo per ciascun host, 12 modelli
  per sessione, sorgenti `nTEST001`–`nTEST036` verificati per hash. Le quattro
  ondate vengono eseguite in sequenza, senza sei host contemporanei.

**Esito finale, SAP2000 27.1.0 e CSiBridge 27.1.0: 144/144 PASS.**

| Host | Applica | Annulla | Sessioni per modalità |
| --- | ---: | ---: | ---: |
| SAP2000 | 36/36 | 36/36 | 3 processi persistenti, 12 modelli ciascuno |
| CSiBridge | 36/36 | 36/36 | 3 processi persistenti, 12 modelli ciascuno |

Risultati isolati in `%TEMP%/opencode/meshing-v303-matrix-20261029-b/`.
`tools/RunMESHingV303Matrix.ps1 -VerifyOnly` ha controllato nuovamente tutti i
gruppi e gli hash delle fixture sorgente. I soli diagnostici `.Y_` opzionali
degli host non sono errori di mesh: i risultati di ogni modello sono stati
verificati separatamente, compresi l'output salvato e riaperto e lo stato
originale dopo Annulla. La DLL del pacchetto, usata nei 144 casi, ha SHA-256
`9E0D106B19ED524CFA403322104A5EBE737645F583D04DD13C0D792B037C7455`.

## Asset previsti per la release

`MESHing-3.0.3.zip`, `MESHing-latest.zip`,
`MESHing-3.0.3-source.zip`, `gmsh-4.15.2-source.tgz`,
`mmg-5.5.2-source.tar.gz`, ciascuno con il proprio `.sha256`: dieci file in
`artifacts/candidate-v3.0.3/`. `SOURCE.md` nello ZIP di installazione elenca
gli URL della medesima release; `packaging/SOURCE_BUILD.md` descrive la build.
