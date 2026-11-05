# Backup NAS MESHing

Percorso confermato: `\\satoshi.b2\csi\MESHing` (non la cartella FEMesh).

- Le versioni **pubblicate** stanno in `\\satoshi.b2\csi\MESHing\<versione>\`:
  ZIP dei sorgenti corrispondenti, sorgenti Gmsh e MMG pubblicati e i loro
  checksum. Erano già presenti i sorgenti MESHing 3.0.0 e 3.0.1; le sette
  release pubbliche da 3.0.0 a 3.0.7 (esclusa la bozza 3.0.3) sono state
  verificate il 3 novembre 2026 contro i digest SHA-256 degli asset GitHub.
  Conservare eventuali altri documenti già presenti nelle cartelle.
- Le versioni **candidate** stanno separatamente in
  `\\satoshi.b2\csi\MESHing\candidate\<versione>\` e possono essere
  cancellate periodicamente dopo i test. La **3.0.15** è la candidata corrente:
  dialoghi Gmsh con z-order corretto e tracciati JSON distinti su più Genera
  nella stessa sessione; dieci asset verificati. SHA-256 installer/alias:
  `e269d9abd8f27e506ca31a881524d5f3453267a4aedada813e5937f1ac52aff5`;
  sorgenti: `d0ad617225a719c3ae08bd84af77a014aefa78771ec9bc2661babb55e5029947`.
  La 3.0.14 rimane una candidata precedente con minimo/default 8; la 3.0.12
  aumentava le 8 richieste a 16 sul modello 40 e non va usata per tale prova.
- La release 3.0.15 **non è ancora pubblicata**. Quando verrà pubblicata,
  archiviarne i sorgenti nella cartella permanente eseguendo di nuovo:

  ```text
  python tools/SyncMESHingNasArchives.py published
  ```

  Lo script enumera soltanto release GitHub non in bozza, confronta gli hash
  degli asset pubblicati, conserva i file già identici e rifiuta di
  sovrascrivere un file NAS diverso. Per copiare/verificare la candidata
  corrente (versione letta dal progetto):

  ```text
  python tools/SyncMESHingNasArchives.py candidate
  ```

  Per una candidata precedente usare `candidate --version X.Y.Z`. Non spostare
  una candidata tra le release pubblicate prima che la relativa release
  GitHub esista e sia verificata. La vecchia cartella
  `\\satoshi.b2\csi\FEMesh` è un archivio distinto e non è stata modificata.
