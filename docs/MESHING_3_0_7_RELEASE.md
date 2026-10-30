# MESHing 3.0.7 — verifica FAST, trasferimento e matrice

## Audit dei passaggi FAST → Standard

| Percorso | Stato in 3.0.7 | Motivo |
| --- | --- | --- |
| Poligono sorgente con ultima riga `Joint` incompleta | FAST su SAP2000/CSiBridge 27.1; altrimenti preflight esplicito | Il poligono sorgente a 5 nodi è ancora presente durante l'importazione; il CSV con soli campi finali vuoti omessi è accettato e verificato dall'host. La mesh finale non contiene poligoni a 5 nodi. |
| `ApplyMeshesFast` quando il getter FAST non è disponibile | Eliminato | Il preflight `SupportsFastTableInsertion` verifica le tre tabelle prima di qualsiasi scrittura: ritorna vero oppure genera errore; il ramo Standard era irraggiungibile. |
| `ApplyMeshFast` su singola area quando il getter FAST non è disponibile | Eliminato | Stesso controllo preliminare; la ripetizione non poteva proteggere una scrittura già iniziata. |
| Preflight della richiesta FAST che disabilitava `directCommit` e catturava CSV | Eliminato | L'Undo ricarica il file SDB/BDB pre-mesh. Un'incompatibilità di tabelle viene ora segnalata prima dell'inserimento, senza cambiare metodo. |
| FAST su CSiBridge 27.1 | Commit numerico diretto e checkpoint SDB/BDB | La precedente restrizione era basata sull'identità del processo ospite, mentre le prove con l'API Bridge utilizzavano già con successo il percorso diretto. |
| Standard esplicito nei casi di compatibilità (`nTEST003`, `nTEST035`) | Conservato | Non è un fallback automatico. Usa tuttora snapshot CSV e API a oggetti; i test della matrice ne verificano la compatibilità. |
| Altri host/versioni non verificati per il CSV poligonale | Errore prima di modificare il modello | Nessun trasferimento silenzioso delle aree figlie via Standard; mantenuta la barriera su tabelle non certificate. |

## Trasferimento madre → figlie

La fixture SAP2000 in `run_test01_fast.cs` e
`tools/TestFEMeshHosts.ps1 -TransferFixture` (CSiBridge) impostano sull'area madre sezione,
materiale sovrascritto, temperatura, massa, assi locali, dieci modificatori
(uno non default), gruppo, carico uniforme, pressione superficiale e di bordo,
molla superficiale e di bordo, vincolo di bordo. Legge i valori in ogni figlio
e verifica la copertura geometrica completa dei carichi e delle molle sul
bordo. Risultato della prova FAST sull'area `A1` di `nTEST001`:
**SAP2000 50/50 e CSiBridge 50/50 figlie verificate**, inclusi nome sezione e
tutti e dieci i modificatori su ogni figlia,
`PARENT_EDGE_ASSIGNMENTS_PASS`, `PARENT_CHILD_ASSIGNMENTS_PASS`.
La variante per la procedura di progetto
esplicita non è dichiarata superata: SAP2000 27.1 restituisce zero righe alla
rilettura della tabella già prima di invocare MESHing. Restano gli avvisi
relativi alle assegnazioni di progetto non leggibili dal relativo host.

## Prove poligonali

- Su una piastra con un'area sorgente a cinque nodi, FAST ha completato
  la generazione evitando lo snapshot CSV e le chiamate Standard per ogni
  elemento. `Annulla mesh` tramite reload SDB e ripristino della selezione:
  PASS. I dati di dettaglio della prova restano fuori dagli asset pubblici.
- `nTEST004`: poligono sorgente su entrambe le API SAP2000/CSiBridge 27.1;
  FAST con riga di continuazione CSV, commit e nomi verificati: PASS.
- `nTEST026`: 3 aree, 12 fori circolari, commit FAST e nomi verificati sui due
  host: PASS.
- `tools/TestV2OversizedSelection.ps1`: selezione di 23.823 shell sospesa e
  riattivazione su 3 aree: PASS. SAP2000 API `SelectObj.All` sulla torre
  meshata seleziona circa 48.800 oggetti; la prova UI interattiva rimane
  distinta dalla misura API.

## Matrice di Genera

La verifica utilizza `tools/RunMESHingV307Matrix.ps1`: 36 casi per SAP2000 e
36 per CSiBridge, tre host persistenti per prodotto e nessuna ondata Undo.
**SAP2000 36/36, CSiBridge 36/36; 72/72 PASS.** Su ciascun host 34 casi FAST
con commit diretto e 2 Standard esplicitamente richiesti (`nTEST003`,
`nTEST035`); **zero fallback automatici**. Verificati risultato dopo riapertura,
nomi definitivi, impostazioni JSON, hash SHA-256 degli input originali e log
dei sei worker.
La matrice è stata ripetuta anche sulla **DLL contenuta nello ZIP installabile**
`artifacts/MESHing-3.0.7.zip`: di nuovo **36/36 SAP2000 + 36/36
CSiBridge**, con il controllo `-VerifyOnly` e lo stesso rapporto
34 FAST + 2 Standard espliciti per host.
Le fixture dettagliate madre→figlie sono passate nuovamente usando la DLL
distribuita: 50 shell SAP2000 e 50 CSiBridge, sezioni e dieci modificatori
compresi. ZIP installabile SHA-256:
`4a14a7efb565500f3858344f2942cfcf754fe942c20218a80d19d458bad5cf99`.
Estrazione, verifica dell'installer e installazione di prova dalla ZIP:
`MESHING_STANDALONE=PASS VERSION=3.0.7.0`.
La matrice usa istanze di test proprie, riconosciute tramite PID; non modifica
le sessioni o i modelli aperti dall'utente. Nessuna ondata di Undo è stata eseguita.
