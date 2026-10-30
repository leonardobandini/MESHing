# MESHing 3.0.5 — release versionata, non Latest

La 3.0.5 è pubblicata come release versionata per prova controllata: lo ZIP è disponibile
solo dalla pagina `https://github.com/leonardobandini/MESHing/releases/tag/v3.0.5`.
Non selezionare la release come **Latest**: il link
`/releases/latest/download/MESHing-latest.zip` e il feed di aggiornamento
devono continuare a puntare alla release stabile già designata.

## Modifiche

- SAP2000 FAST: salvataggio pre-mesh, protezione del file, nomi numerici
  definitivi per nodi e shell, cancellazione batch degli originali in Genera;
  Applica conferma, Annulla mesh riapre l'SDB pre-mesh senza salvare la mesh.
- L'annullamento **durante** Genera ricarica lo stato appena salvato anche con
  FAST disattivato e in CSiBridge, quando la chiamata API corrente si conclude.
- Il messaggio "Mesh completata. Elementi area creati" non presenta più il
  tooltip esteso. Le opzioni di trasferimento, la persistenza delle schede
  e la relazione fra finestra host e finestre di altre applicazioni sono
  corrette nella stessa versione.
- Il backup numerato opzionale continua a copiare il file **prima** del
  salvataggio pre-mesh. La copia privata del file pre-mesh usata per il
  ripristino è distinta e viene eliminata dopo la conferma o il ripristino.
- Il CSV tabellare resta disponibile per Annulla mesh del percorso Standard
  e per le anteprime in CSiBridge; nella fase Annulla generazione il ripristino
  usa il modello salvato.

## Distribuzione e ripristino

Pacchetto installabile `MESHing-3.0.5.zip`, sorgenti corrispondenti
`MESHing-3.0.5-source.zip`, sorgenti originali Gmsh e MMG con checksum SHA-256
sono asset della stessa pagina versionata. La release non include un asset
`MESHing-latest.zip`; non aggiornare il workflow del link mobile.

## Verifiche prima del rilascio

La matrice completa su copie isolate è riuscita: tre SAP2000 27 e tre
CSiBridge 27 contemporanei durante ciascuna ondata, **36/36 Apply + 36/36
Undo per host, 144/144 casi**. Le matrici hanno usato il pacchetto completo
3.0.5, incluso il backend 3.0.5, e controllato che gli SDB/BDB originali
restassero invariati.

Quattro prove mirate di Annulla generazione hanno richiesto l'annullamento
dopo l'avvio del backend Gmsh: SAP2000 FAST, SAP2000 Standard, CSiBridge FAST
e CSiBridge Standard. Tutte hanno riaperto il modello pre-mesh sullo stesso
percorso e ripristinato area, GUID e selezione; nessuna ha salvato la mesh
annullata. Questa verifica è distinta dai 144 casi Apply/Undo della matrice.

Tracce locali in `%TEMP%\opencode\meshing-v305-six-host-matrix-20261030\`
e `%TEMP%\opencode\meshing-v305-cancel-*-{sap,bridge}\`.

La baseline locale precedente alle modifiche transazionali è nel commit
`da84469f83a049d78e2420c5983de0482f5d4bdd`. Non includere nei commit
né negli ZIP i riferimenti storici V2 o i modelli locali dell'utente.
