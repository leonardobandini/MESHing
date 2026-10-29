# MESHing 3.0.4 — release per verifica controllata

La 3.0.4 è pubblicata come release normale ma non è contrassegnata "Latest";
il suo link diretto può essere condiviso con i tester. È comunque visibile
anche nell'elenco pubblico delle release GitHub. **La release
stabile e il download `MESHing-latest.zip` restano sulla 3.0.2**: le versioni
installate 3.0.2 e 3.0.3 non vengono avvisate della 3.0.4 dal controllo
aggiornamenti, che legge soltanto la release stabile. Il tag `v3.0.4` è
visibile nel repository, ma non è considerato dal feed `releases/latest` finché
la release non viene impostata "Latest".
La 3.0.3 rimane ritirata in bozza.

## Modifiche

- Risolto il rallentamento dello snapshot provocato dalla lettura ripetuta del
  nome e dell'ora di avvio del processo host. Questi dati immutabili vengono
  acquisiti una sola volta; l'ora dell'ultima modifica delle impostazioni
  internazionali viene invece sempre verificata nel registro. Su copia di
  `Circular_Floor` con 50 cm, lo snapshot è sceso da 61–113 secondi della
  build 3.0.3 ritirata a 9,6–14,6 secondi nelle prove della correzione;
  la generazione con Annulla ha ripristinato la selezione di 465 oggetti.
- La finestra mostra "Acquisizione dello snapshot" dopo la fase di salvataggio,
  anziché lasciare il precedente messaggio di salvataggio durante la lettura
  delle tabelle.
- I dialoghi delle opzioni MeshAdapt, BAMG e dell'ottimizzazione Gmsh
  rispettano l'ordine **opzioni → MESHing → SAP2000/CSiBridge → altre
  finestre** senza attivare l'host; MESHing è proprietario del dialogo.

L'identità COM resta invariata. Lo snapshot CSV conserva tutte le tabelle
necessarie per Annulla; `GetAllTables` dell'API CSI elenca le tabelle, ma non
esporta in una sola chiamata i dati di tutte le tabelle.

## Verifiche prima della pubblicazione

- Build plugin e installer `net48` x64, verifica backend Gmsh 4.15.2,
  licenze e sorgenti corrispondenti, installer standalone e dieci asset con
  i rispettivi checksum SHA-256.
- Matrice atomica: 36 casi Applica e 36 Annulla su SAP2000; 36 Applica e 36
  Annulla su CSiBridge. Ogni ondata avvia **tre SAP2000 e tre CSiBridge
  contemporaneamente**, con dodici modelli isolati per processo e hash delle
  fixture sorgente controllati prima/dopo.

**Esito finale: 144/144 PASS.**

| Host | Applica | Annulla | Concorrenza per ondata |
| --- | ---: | ---: | --- |
| SAP2000 27.1.0 | 36/36 | 36/36 | 3 processi persistenti |
| CSiBridge 27.1.0 | 36/36 | 36/36 | 3 processi persistenti |

In ogni ondata erano attivi **contemporaneamente tre processi SAP2000 e tre
CSiBridge**. Le copie dei modelli e i log sono in
`%TEMP%/opencode/meshing-v304-six-host-matrix-20261029/`; il verificatore
`tools/RunMESHingV304Matrix.ps1 -VerifyOnly` ha confermato separatamente tutti
i gruppi, le modalità e gli hash delle fixture. I diagnostici opzionali `.Y_`
di SAP/Bridge non sono stati considerati errori solo dopo aver verificato
**tutti i modelli** e il salvataggio/riapertura degli output. La DLL testata e
installata localmente ha SHA-256
`2FC7892181CBBC81306A210DAF947756FB096A928379F5624791C628CAEA9C2D`.
Per rendere la 3.0.4 l'ultima release stabile dopo le prove degli utenti basta
impostare **la stessa release** "Latest": non serve cambiare tag, versione,
binari, checksum o asset.

## Asset

`MESHing-3.0.4.zip`, `MESHing-3.0.4-source.zip`, sorgenti Gmsh e MMG,
`MESHing-latest.zip` allegato alla medesima release, ognuno con il
proprio `.sha256`: dieci file nel candidato
`artifacts/candidate-v3.0.4/`. Finché la 3.0.4 non è "Latest", il collegamento mobile
`/releases/latest/download/MESHing-latest.zip` continua a servire la 3.0.2;
lo ZIP 3.0.4 va scaricato dalla pagina della release 3.0.4.
