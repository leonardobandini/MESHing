# MESHing — changelog

## MESHing 3.0.7

- FAST non passa più silenziosamente all'inserimento Standard quando una tabella non è supportata: il preflight interrompe Genera prima di modificare il modello. Standard rimane una modalità esplicita per i test di compatibilità.
- Le righe di continuazione dei poligoni sorgente in SAP2000 e CSiBridge 27.1 sono importate come CSV senza i soli campi vuoti finali; nodi, ordine e GUID del poligono sono verificati prima del commit. Anche una selezione con un'area iniziale a cinque nodi conserva FAST e non richiede lo snapshot CSV legacy.
- Il commit FAST con nomi numerici definitivi e ripristino tramite SDB/BDB pre-mesh è disponibile in SAP2000 e CSiBridge. La selezione massiva dopo Genera non ricalcola migliaia di contesti via COM a ogni aggiornamento; la finestra riprende quando si seleziona un insieme gestibile.
- I trasferimenti madre→figli di proprietà, gruppi, carichi, molle, assi, massa, modificatori e vincoli sono verificati su ogni shell della fixture dedicata; i limiti delle procedure di progetto non leggibili restano segnalati.
- Matrice completa Genera su 36 casi SAP2000 e 36 CSiBridge: 72/72 PASS, senza Undo; per host 34 trasferimenti FAST e 2 Standard esplicitamente richiesti, nessun fallback automatico. Fixture dettagliata madre→figlie: 50/50 shell verificate per host.

## MESHing 3.0.6 — release Latest

- Genera completa anche l'applicazione della mesh: non esiste più la commutazione del pulsante in Applica. Annulla mesh resta disponibile per la generazione più recente fino a una nuova generazione o alla chiusura del plugin.
- Con una nuova selezione di aree, Genera si riabilita. Prima di una generazione successiva nello stesso modello compare un avviso OK/Annulla, disattivabile per la sessione: il modello viene salvato e l'Undo precedente è sostituito da quello della nuova mesh.
- Chiudendo MESHing dopo Genera viene avvisato che l'Undo non sarà più disponibile; la mesh è ancora in memoria e chiudere il modello host senza salvarlo riapre lo stato su disco precedente all'ultima generazione.
- Dopo Annulla mesh, il riblocco del file pre-mesh riaperto è trattato come parte del ripristino del plugin: non deve far comparire l'avviso di sblocco manuale in assenza di un intervento dell'utente.
- Il riepilogo di elementi creati e tempo impiegato rimane visibile dopo Genera e l'applicazione automatica, fino alla selezione di una nuova area.
- La scheda Opzioni di backup contiene soltanto il backup numerato, attivo per impostazione predefinita. FAST è automatico dove supportato; restano l'inserimento di compatibilità e gli avvisi per backup disattivato.
- La matrice ridotta di rilascio ha passato 6/6 Genera in SAP2000 e 6/6 in CSiBridge con tre istanze concorrenti per host; la prova mirata di due Genera nello stesso modello SAP2000 è riuscita. Per la release precedente la matrice completa Apply/Undo aveva verificato 144/144 casi.

## MESHing 3.0.5 — release pubblicata, non contrassegnata "Latest"

- In SAP2000 FAST Genera inserisce direttamente nodi/aree numerati e rimuove in batch le aree originali; Applica conferma la mesh e Annulla mesh ricarica l'SDB salvato prima della generazione. Se l'SDB è stato modificato sul disco, il ripristino si ferma e conserva la copia privata.
- Il comando Annulla generazione ricarica il modello pre-mesh anche in Standard e CSiBridge, appena termina la chiamata API in corso. La richiesta di annullamento rimane registrata fino alla verifica finale.
- Eliminato il tooltip esteso dal messaggio di completamento; corretti testi di trasferimento, preferenze dei tab e ordine delle finestre rispetto alle altre applicazioni.
- Scaricabile soltanto dalla pagina della versione; `Latest`, il link statico e il feed degli aggiornamenti restano sulla release stabile selezionata.

## MESHing 3.0.4 — release non contrassegnata "Latest"

- Risolto il rallentamento dello snapshot introdotto dal controllo delle impostazioni internazionali della 3.0.3: il nome e l'ora di avvio del processo ospite sono acquisiti una sola volta, mentre la data di modifica del registro resta controllata durante il lavoro. Su `Circular_Floor` a 50 cm lo snapshot è passato dai 61–113 s osservati nella 3.0.3 a 9,6–14,6 s nella build corretta; senza profilatore 9,6 s. Il salvataggio e lo snapshot hanno ora messaggi distinti nell'interfaccia.
- Aprendo le impostazioni di MeshAdapt, BAMG o dell'ottimizzatore Gmsh, le finestre rispettano l'ordine opzioni → MESHing → host senza far passare SAP2000/CSiBridge sotto le altre applicazioni.
- La release 3.0.3 rimane ritirata in bozza; la 3.0.4 è una release normale, visibile nell'elenco GitHub ma non "Latest". `MESHing-latest.zip` e il feed degli aggiornamenti continuano a puntare alla 3.0.2 finché la stessa release 3.0.4 non viene contrassegnata manualmente "Latest", senza nuovo bump né nuovi asset.
- Risultati delle regressioni di rilascio in `docs/MESHING_3_0_4_RELEASE.md`.

## MESHing 3.0.3 — impostazioni della sessione e protezione del formato regionale

- Alla riapertura, le opzioni della mesh tornano ai default; rimangono soltanto le dimensioni minima/massima e l'ultima lingua scelta. Senza preferenza salvata si usa la lingua dell'interfaccia di sistema. Durante la stessa sessione aperta le opzioni restano disponibili per generazioni consecutive.
- Nelle opzioni di trasferimento, la voce FAST in italiano è allineata in alto con il checkbox e non taglia il testo su due righe.
- Prima di aprire il plugin e durante la sessione viene controllata l'ultima modifica delle impostazioni internazionali di Windows rispetto all'avvio del processo SAP2000 o CSiBridge. In caso di cambio, il messaggio nomina solo l'host corrente e chiede di chiudere modello e host senza salvare, quindi di riavviare l'applicazione. Durante la generazione vengono bloccati Applica, Annulla e i rollback incompatibili; se il salvataggio pre-mesh era in corso viene segnalata anche la verifica del file salvato/backup.
- Rilascio stabile, non una build di test. La matrice di regressione è composta da 36 casi Apply e 36 Undo su ciascuno dei due host, ripartiti su tre processi persistenti per host; i risultati finali della build pubblicata sono documentati in `docs/MESHING_3_0_3_RELEASE.md`.

## MESHing 3.0.2 — finestre delle impostazioni e formati numerici SAP2000/CSiBridge

- I dialoghi modali delle impostazioni degli algoritmi di mesh e ottimizzazione restano davanti alla finestra principale fino a OK o Annulla; il blocco dell'host durante il dialogo è preservato.
- I valori numerici nuovi inseriti nelle tabelle dell'host adottano il separatore decimale effettivo di Windows (punto o virgola), senza separatore delle migliaia. CSV e JSON scambiati con Gmsh rimangono invarianti. Il ripristino della temperatura dei materiali interpreta entrambi i separatori.
- Con separatore decimale Windows `,`: matrice di generazione/Applica su copie **36/36 SAP2000 e 36/36 CSiBridge**, ripartita inizialmente su tre processi persistenti per host; `nTEST006` SAP, scaduto durante il salvataggio/diagramma con sei host concorrenti, è passato nella ripetizione dei casi 006–012. Prove Annulla dell'anteprima **3/3 per host** (001, 004, 014; FAST e Standard). Formattazione numerica verificata anche con `.`; tre dialoghi modali verificati con OK/Annulla.

## MESHing 3.0.1 — rilevamento delle installazioni non standard

- L'installer individua SAP2000 e CSiBridge 27+ anche quando Windows ne registra un percorso `InstallLocation` diverso da `Program Files`; verifica eseguibile e API prima di aggiornare i menu nei profili già inizializzati.
- La ricerca nella cartella standard rimane disponibile; se non trova alcun host compatibile, l'installer lo segnala invece di omettere silenziosamente la registrazione del menu. Il motore di mesh e l'identità COM restano invariati.
- L'utente ha confermato una prova riuscita con SAP2000 installato in un percorso non standard. Per CSiBridge nello stesso scenario sono disponibili soltanto prove su installazioni e profili simulati.

## MESHing 3.0.0 — prima release con il nuovo nome

- Nuova identità pubblica MESHing e installer con una sola voce di menu MESHing; l'identità COM tecnica FEMesh resta compatibile con le installazioni SAP2000 e CSiBridge già esistenti.
- L'updater del nuovo client consulta soltanto le release MESHing e apre il download del relativo ZIP soltanto dopo il clic dell'utente. Nessuna release o asset FEMesh viene modificato.
- Nuovo logo MESHing nell'help IT/EN; la finestra principale conserva layout, sottotitolo «Advanced 2D mesh generation» e logo CSi Italia.

## 2.0.68 — 25 ottobre 2026

- Un tentativo di sbloccare il modello mentre FEMesh è aperto richiude il lucchetto e mostra un avviso modale anche ai tentativi successivi: premere OK conserva l'anteprima, i comandi Applica/Annulla e la possibilità di zoom e pan senza provocare un errore di Annulla. Durante Genera, Applica e Annulla e mentre sono aperte domande o avvisi del plugin, l'interazione con il modello è bloccata. Verificati i cicli ripetuti di avviso e la doppia matrice Applica/Annulla su SAP2000 e CSiBridge (144/144 casi).

## 2.0.67 — 25 ottobre 2026

- Corretta la meshatura simultanea di solai e pareti quando un'intersezione parete–solaio attraversa un'apertura: nella cella del solaio rimossa non vengono più richiesti nodi e tratti di intersezione interni, mentre restano verificati il bordo dell'apertura e i vincoli delle aree verticali e dei tratti esterni. Verificati Applica e Annulla mesh su copia del modello a 110 aree, con 23.824 elementi generati.

## 2.0.66 — release stabile pubblicata il 25 ottobre 2026

- Corretto il riconoscimento dei bordi di un'apertura quando frame del gruppo si sovrappongono a frame selezionati non appartenenti al gruppo: i tratti comuni mantengono l'appartenenza geometrica al contorno dopo la suddivisione.
- Durante ogni Genera, backup numerato opzionale prima del salvataggio obbligatorio dello stato corrente; lo snapshot di Annulla segue il salvataggio. Documentati gli effetti su Applica e Annulla mesh.
- Un modello mai salvato mostra un avviso all'apertura del plugin: guida e schede restano consultabili ma le operazioni sono disattivate finché il modello non viene salvato nell'host.
- L'installer verifica la versione del Visual C++ Redistributable x64 e, se necessario, scarica da Microsoft il prerequisito firmato e ne avvia il setup interattivo prima di installare FEMesh.
- Nuovo installer grafico bilingue con lingua Windows rilevata automaticamente, selezione manuale IT/EN e stato delle fasi fino alla registrazione nei menu SAP2000/CSiBridge.
- Il modello resta bloccato mentre FEMesh è aperto; le modifiche del plugin sbloccano temporaneamente il modello con la finestra SAP2000/CSiBridge non interattiva. Corretta la riabilitazione della finestra dell'host alla chiusura.

## 2.0.65 — 23 ottobre 2026

- Ripristinato il controllo aggiornamenti della V2, con la medesima UI e gli stessi messaggi; consulta esclusivamente l'ultima release stabile pubblica di FEMesh.
- Il download avviene soltanto su scelta esplicita dell'utente tramite browser; nessuna installazione automatica.
- Lo ZIP dell'ultima release usa il nome stabile `FEMesh-latest.zip`.

## 2.0.64 — 23 ottobre 2026

- `SOURCE.md` identifica esclusivamente binario, sorgenti e checksum della medesima release FEMesh.
- Disattivato il controllo aggiornamenti integrato e rimosso il relativo collegamento al feed; le future versioni saranno annunciate tramite le release FEMesh.
- Nessuna modifica prevista all'algoritmo di mesh.

## 2.0.63 — 22 ottobre 2026

- Distribuzione del medesimo motore di mesh verificato nella precedente matrice atomica: 36/36 su SAP2000, 36/36 su CSiBridge.
- Installer pubblico `FEMesh-2.0.63.zip` e collegamento stabile `FEMesh-latest.zip`, mantenendo gli alias tecnici necessari ai client degli aggiornamenti esistenti.
- Licenze e sorgenti corrispondenti verificati; il binario combinato usa GPLv3, mentre il codice originale FEMesh e Gmsh conservano GPL-2.0-or-later. L'installer include `SOURCE.md`; la build dal solo archivio sorgenti è stata provata.
