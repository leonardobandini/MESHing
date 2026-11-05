# MESHing — stato di consegna (30 ottobre 2026)

Leggere questo file all'inizio della prossima sessione; verificare sempre lo
stato corrente di Git/GitHub prima di agire, perché può essere cambiato.

## Stato corrente: MESHing 3.0.7

- **MESHing 3.0.7 è pubblicata come Latest**:
  https://github.com/leonardobandini/MESHing/releases/tag/v3.0.7
- `origin/main` e il tag annotato `v3.0.7` puntano al commit
  `404743963c2a8f65a3e64b2c0c68adfde1452bda`. Autore, committer e
  tagger: `leonardobandini <86308008+leonardobandini@users.noreply.github.com>`.
- Dieci asset pubblicati con SHA-256 GitHub verificati contro i file in
  `artifacts/candidate-v3.0.7/`. Installer e alias Latest identici:
  `4a14a7efb565500f3858344f2942cfcf754fe942c20218a80d19d458bad5cf99`;
  ZIP sorgenti: `c3bb4f6864b1044bf16581a6c0ad16aeb775ba23ed47d7475dac0c6954f98333`.
  Il link `/releases/latest/download/MESHing-latest.zip` risponde HTTP 200.
- Installazione 3.0.7 sul PC verificata; test 36/36 SAP2000 e 36/36
  CSiBridge sulla DLL dello ZIP, senza Undo. Solo `AGENTS.md` e riferimenti
  storici/report/strumenti non tracciati rimangono fuori dal commit/release.

## Stato storico precedente: MESHing 3.0.6

- **MESHing 3.0.6** era pubblicata e contrassegnata **Latest**:
  https://github.com/leonardobandini/MESHing/releases/tag/v3.0.6
- Download mobile:
  https://github.com/leonardobandini/MESHing/releases/latest/download/MESHing-latest.zip
- Commit `232a0a7ec9d2a1029d375f783538dedff9118583` su `origin/main`;
  il tag `v3.0.6` punta al medesimo commit. Autore e committer verificati:
  `leonardobandini <86308008+leonardobandini@users.noreply.github.com>`.
- La release ha **10 asset**: ZIP installabile 3.0.6, alias
  `MESHing-latest.zip` identico, ZIP sorgenti, sorgenti Gmsh/MMG e i cinque
  rispettivi checksum. I digest GitHub coincidevano con tutti i file locali.
  Il link `/releases/latest/download/MESHing-latest.zip` rispondeva con
  redirect alla 3.0.6 e HTTP 200. La versione era installata anche localmente.

## Comportamento della 3.0.6

- **Genera** completa anche l'applicazione in memoria; il pulsante non cambia
  più in Applica. FAST è automatico quando supportato; il tab **Opzioni di
  backup** contiene la sola opzione backup numerato, inizialmente attiva.
- **Annulla mesh** riapre il modello salvato subito prima dell'ultimo Genera.
  Una nuova generazione dopo selezione di un'area chiede OK/Annulla e può
  sopprimere l'avviso nella sessione; il nuovo salvataggio sostituisce il
  precedente punto di Undo. Chiudere il plugin avvisa che l'Undo non sarà più
  disponibile; la mesh non viene salvata nell'host automaticamente.
- Corretto il falso avviso di sblocco dopo Undo causato dal reload gestito dal
  plugin. Il riepilogo di elementi creati e tempo impiegato rimane visibile
  fino alla selezione di una nuova area. Help IT/EN aggiornato.

## Verifiche della release

- Matrice ridotta **solo Genera**, tre SAP2000 27 e tre CSiBridge 27
  contemporanei, due casi per host: `nTEST006`, `020`, `024`, `025`, `028`,
  `033`. **6/6 SAP + 6/6 Bridge, 12/12 PASS**; originale SDB invariato.
  Script `tools/RunMESHingV306ReducedMatrix.ps1`, risultati locali in
  `%TEMP%\opencode\meshing-v306-reduced-six-host-20261030\`.
- Prova di due Genera consecutivi nello stesso modello SAP2000: PASS, risultati
  in `%TEMP%\opencode\meshing-v306-same-model-next-generate-final2\`.
- Test UI del riepilogo, verifica help e prova mirata del riblocco dopo
  `OpenFile` passati. Non è stata ripetuta l'ondata Annulla nella matrice
  ridotta: era stata verificata separatamente nella matrice completa precedente.
- Dettagli in `docs/MESHING_3_0_6_RELEASE.md` e `CHANGELOG.md`.

## Workspace e prossimo passo

- Al termine della pubblicazione `main` era sincronizzato con `origin/main`;
  rimangono file **non tracciati preesistenti** (documenti storici V2, modelli,
  strumenti/report locali). Non aggiungerli automaticamente a Git né agli ZIP.
- Questo handoff e il riferimento in `AGENTS.md` sono modifiche locali della
  sessione corrente, **non ancora committate né pubblicate**.
- Non ci sono ulteriori azioni richieste sulla release 3.0.6: attendere il
  prossimo riscontro/richiesta dell'utente prima di cambiare codice o release.

## Aggiornamento: MESHing 3.0.7 (30 ottobre 2026)

- La versione 3.0.7 è pronta nei dieci asset di
  `artifacts/candidate-v3.0.7/`. ZIP installabile e alias Latest identici:
  SHA-256 `4a14a7efb565500f3858344f2942cfcf754fe942c20218a80d19d458bad5cf99`.
  Archivio sorgenti corrispondenti:
  SHA-256 definitivo `c3bb4f6864b1044bf16581a6c0ad16aeb775ba23ed47d7475dac0c6954f98333`.
  Lo ZIP installabile è stato estratto, verificato e installato in prova;
  passaggio 3.0.6→3.0.7 verificato.
- Audit FAST→Standard in `docs/MESHING_3_0_7_RELEASE.md`: eliminati i
  fallback automatici; FAST usa CSV verificato per la riga di continuazione
  di un poligono sorgente su SAP2000/CSiBridge 27.1. Standard rimane soltanto
  nei due casi atomici che lo richiedono esplicitamente. La fixture completa
  madre→figlie ha passato 50/50 shell per ciascun host; la procedura di
  progetto esplicita non è verificabile nel fixture perché l'API SAP restituisce
  zero righe già prima di chiamare il plugin.
- Matrice **36/36 SAP + 36/36 CSiBridge, 72/72 PASS, senza Undo** ripetuta
  sulla DLL distribuita nello ZIP. Log sotto
  `%TEMP%\opencode\meshing-v307-package-full-apply-20261030\`; 34 FAST con
  commit diretto e 2 Standard espliciti per host, zero fallback. Eseguita
  anche sulla DLL di preparazione con esito 72/72. Originali atomici invariati.
- Commit, tag, push e release GitHub 3.0.7 eseguiti; 3.0.7 è Latest.
  I riferimenti storici V2 non tracciati e gli script relativi ai modelli
  privati 37 non appartengono alla release.
- La build 3.0.7 è stata installata da `artifacts/candidate-v3.0.7/MESHing-3.0.7.zip`
  in `%LOCALAPPDATA%\Programs\SAP2000QuadMesher`: installer `verify` PASS,
  DLL installata identica a quella del pacchetto, backend `--version` 3.0.7,
  COM e menu SAP2000/CSiBridge aggiornati. Copia precedente verificata in
  `%TEMP%\opencode\meshing-before-307-install-7f29e916dcd14e6d9a44835d5c675239`.

## Aggiornamento locale: candidata 3.0.12 e NAS (3 novembre 2026)

- La candidata **3.0.12 non è pubblicata**: dieci asset con checksum sotto
  `artifacts/candidate-v3.0.12/`; installer ZIP SHA-256
  `81595848ace09c027fbb7a7507ff495b34ff57320a02f372ad69a671599d52f3`.
  Default: 12 corde minime e O-grid larga 0,5 diametri. Il modello 40 su copia,
  ZIP estratto e test standalone hanno passato le prove. Aggiornamento locale:
  la 3.0.12 è stata installata dallo ZIP della candidata dopo verifica SHA-256;
  DLL, script Gmsh e backend installati coincidono con lo ZIP. Installer
  `verify` PASS, backend `--version` 3.0.12, COM e menu SAP2000/CSiBridge aggiornati.
- NAS confermato: `\\satoshi.b2\csi\MESHing`. Le sette versioni MESHing
  pubblicate (3.0.0, 3.0.1, 3.0.2, 3.0.4–3.0.7) sono ora archiviate con
  sorgenti Gmsh/MMG e checksum; la bozza 3.0.3 è esclusa. La cartella
  `candidate\3.0.12` è separata e periodicamente eliminabile. Dettagli e
  procedura di aggiornamento in `docs/NAS_BACKUP.md` e
  `tools/SyncMESHingNasArchives.py`. Nessun file della vecchia cartella NAS
  FEMesh è stato modificato.

## Aggiornamento locale: candidata 3.0.13 (3 novembre 2026)

- Corretto il conteggio O-grid: le corde della circonferenza interna crescono
  solo se la dimensione massima richiede corde più corte *sul foro* o per
  allineare giunti/simmetria; il bordo esterno può superare la dimensione
  nominale. Sul modello 40 su copie: 8 richieste → **8 su tutti gli otto fori**,
  default 12 → **12 su tutti**, con importazione FAST riuscita in entrambe le
  prove. Test Python 35 PASS, 1 skip; ZIP estratto/test-install PASS.
- `artifacts/candidate-v3.0.13/` contiene dieci asset locali non pubblicati,
  copiati e riverificati anche in `\\satoshi.b2\csi\MESHing\candidate\3.0.13`.
  Installer/alias SHA-256
  `e5a4087947dfec72ad74b9a3a2dc8f788a3873976d155338cf8ebd27edac6995`.
  La 3.0.12 resta sul NAS come candidata superata, non pubblicata.
- La sessione SAP2000 aperta dall'utente sul modello originale 40 è stata
  chiusa **senza salvare** tramite `ApplicationExit(false)` (codice 0) dopo
  test eseguiti in istanze isolate su copie. La candidata 3.0.13 è stata
  installata dallo ZIP locale: installer `verify` PASS; DLL, script Gmsh e
  backend identici allo ZIP; backend `--version` 3.0.13, COM e menu aggiornati.

## Aggiornamento locale: candidata 3.0.14 (3 novembre 2026)

- Minimo accettato e default del numero di corde portati entrambi a 8 in UI
  e backend; la larghezza O-grid default rimane 0,5 diametri. Sul modello 40
  su copia: **8 corde su tutti gli otto fori** e importazione FAST riuscita.
  Test Python 35 PASS, 1 skip, ZIP estratto/test-install PASS.
- `artifacts/candidate-v3.0.14/` contiene dieci asset locali non pubblicati;
  ZIP installer/alias SHA-256
  `72081d1d7cbff69952f5d7cc486101c2659acd9dba470f05bbb3fe772967e3b5`.
  Copia verificata sul NAS in `\\satoshi.b2\csi\MESHing\candidate\3.0.14`.
  Le candidate precedenti restano separate dall'archivio pubblico.
- La 3.0.14 è installata dallo ZIP della candidata: installer `verify` PASS,
  DLL/script Gmsh/backend identici allo ZIP, backend `--version` 3.0.14,
  COM e menu SAP2000/CSiBridge aggiornati. Non sono stati eseguiti commit né
  pubblicazioni GitHub.

## Aggiornamento locale: candidata 3.0.15 (4 novembre 2026)

- Z-order: riprodotta la regressione con finestre normali (non TopMost), in cui
  SAP2000 saliva sopra impostazioni e MESHing. `ShowHostModalFromPanel` mantiene
  ora SAP direttamente dietro al proprietario. Test sintetico sulla DLL
  installata: 3 finestre modali per oltre un secondo, PASS con proprietario
  normale e TopMost, ordine impostazioni → MESHing → host → altre applicazioni.
- Modello 41: prima mesh con 10 mm, foro BOLTS O-grid 20 mm e gruppo SHAFT
  applicata su copia. Durante il primo tentativo GUI/Excel nella stessa
  sessione, l'errore osservato era **JSON di tracing già esistente**, perché
  SAP aveva ereditato il percorso di tracing fisso dal processo di test; non
  dimostrava una corruzione delle tabelle. Il tracing ora conserva il primo
  JSON e assegna un nome univoco al successivo. Nella prova manuale successiva
  su una sessione pulita, l'utente ha completato un secondo Genera dopo la
  tabella GUI ed Excel, senza errore tabellare. Prova separata su DLL 3.0.15:
  due Genera in un unico processo SAP su due copie, lettura tabellare OAPI fra
  i due, 2/2 PASS e due JSON distinti. Non è stato modificato il protocollo
  FAST in assenza di un errore di sincronizzazione riprodotto.
- I tentativi manuali hanno provocato un salvataggio pre-mesh nel file originale
  `41\Steel_Plate.sdb`; esso è stato ripristinato dai backup verificati,
  SHA-256 originale `99D25BDA9AF2BDE2B366DCE65FFD877003F796A20A506E8F64195063637244A1`.
  I due stati modificati sono conservati sotto `%TEMP%\opencode\meshing41-...`
  come copie diagnostiche; i backup numerati creati in `41` non sono stati
  eliminati. Bitdefender ha bloccato la DLL temporanea `UIProbeInterop.dll`:
  non reiterare test GUI basati su DLL P/Invoke generate al volo.
- `artifacts/candidate-v3.0.15/` e
  `\\satoshi.b2\csi\MESHing\candidate\3.0.15` contengono i dieci asset
  non pubblicati, ZIP installer/alias SHA-256
  `e269d9abd8f27e506ca31a881524d5f3453267a4aedada813e5937f1ac52aff5`.
  La candidata 3.0.15 è installata localmente; `verify` PASS, DLL/script/backend
  identici allo ZIP, backend `--version` 3.0.15 e COM verificata. La release
  GitHub Latest resta 3.0.7.

## MCP del repository gemello (sessione successiva)

- L'utente chiede che MESHing usi il MCP versionato nel repository fratello
  `../csi-fem-assistant`. Il file locale e Git-ignored `opencode.json` punta
  alla sua `.venv` e imposta `PYTHONPATH` al repository sorgente; non e' stata
  copiata alcuna implementazione MCP in MESHing. `AGENTS.md` contiene la regola.
- Verificati `opencode mcp list` (server connesso) e un vero client MCP stdio
  da MESHing: `csi_get_system_status()` risponde versione **0.14.0**, 20 tool.
  Il wrapper `comtypes.gen.CSiAPIv1` del venv era obsoleto rispetto alla
  typelib CSI installata: rigenerato puntualmente tramite `GetModule` sul file
  `SAP2000 27/NativeAPI/x64/CSiAPIv1.tlb`, senza modificare i sorgenti gemelli.
  Riavviare OpenCode prima di aspettarsi i tool MCP nella sessione: la
  configurazione non viene ricaricata a caldo.
