# MESHing 3.0.6 — release Latest

La 3.0.6 è la release Latest: ZIP versionato e alias identico
`MESHing-latest.zip` sono sulla stessa pagina. Il feed del plugin e il link
mobile puntano alla release 3.0.6.

## Modifiche

- Genera completa la mesh nel modello in memoria, senza un secondo Applica.
  In SAP2000 FAST si mantengono nomi numerici definitivi e rimozione batch
  degli originali; nei casi incompatibili e in Bridge rimane il percorso
  di inserimento compatibile, completato automaticamente.
- Dopo Genera l'Undo di SAP2000 e CSiBridge riapre il file pre-mesh salvato,
  anche per le aree invariate o inserite con il percorso Standard; il CSV
  resta per i ripristini durante un inserimento compatibile non riuscito.
- Annulla mesh riguarda l'ultima generazione finché MESHing è aperto; appena
  viene selezionata un'altra area Genera è disponibile. Il Genera successivo
  avvisa che salverà il modello corrente e sostituirà l'Undo precedente:
  OK/Annulla, con opzione per non mostrare più l'avviso nella sessione.
- Alla chiusura dopo Genera compare un avviso: l'Undo nel plugin non sarà più
  disponibile. Il modello con la mesh non è stato ancora salvato sul disco;
  chiudere il modello host senza salvare riporta allo stato sul disco precedente
  all'ultima generazione.
- Dopo Annulla mesh il blocco del modello riaperto viene ristabilito prima
  della rilettura della selezione; un eventuale sblocco dovuto a `OpenFile`
  non viene erroneamente attribuito all'utente.
- Dopo Genera il numero di elementi e il tempo impiegato restano nella barra
  di stato, anche dopo la conferma automatica, finché non è selezionata una
  nuova area.
- La scheda Opzioni di backup contiene la sola opzione per il backup numerato,
  inizialmente attiva. FAST è automatico dove supportato; gli avvisi esistenti
  per backup disattivato restano invariati.
- Help italiano e inglese aggiornati prima della compilazione del pacchetto.

## Distribuzione

Asset: `MESHing-3.0.6.zip`, `MESHing-latest.zip` identico, archivio
`MESHing-3.0.6-source.zip`, sorgenti Gmsh 4.15.2 e MMG 5.5.2, ognuno con
SHA-256. Dieci asset preparati in `artifacts/candidate-v3.0.6/`.

## Verifiche

Matrice ridotta su copie isolate, una sola ondata con tre SAP2000 27 e tre
CSiBridge 27 contemporanei (due casi per processo): **6/6 Genera SAP2000 +
6/6 Genera CSiBridge, 12/12 PASS**. Casi `nTEST006`, `nTEST020`, `nTEST024`,
`nTEST025`, `nTEST028`, `nTEST033`: fori O-grid multipli, aperture complesse,
simmetria, molti elementi e bordi densamente vincolati. I modelli sorgente
sono rimasti invariati. Per queste piccole modifiche non è stata ripetuta
l'ondata Annulla; la riapertura del file pre-mesh era stata verificata nella
precedente matrice completa. Prova mirata di due Genera consecutivi sullo
stesso modello SAP2000 in una sessione: PASS; il secondo Genera sostituisce
il precedente Undo. La verifica UI del riepilogo elementi/tempo e la prova
di riblocco dopo la riapertura del file hanno passato i controlli mirati.

Tracce locali in `%TEMP%\opencode\meshing-v306-reduced-six-host-20261030\`
e `%TEMP%\opencode\meshing-v306-same-model-next-generate-final2\`.
