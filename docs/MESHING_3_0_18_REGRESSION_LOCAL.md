# MESHing 3.0.18 — regressione locale (9 novembre 2026)

**Prove sulla prima candidata 3.0.18.** Le prove complete sotto usavano la DLL
della prima candidata, installata e verificata rispetto allo ZIP con SHA-256
`5b3da022d6e1b255e6c0bb7acbd2bda243d8720210c13709b237782451294b9c`.
La candidata attualmente in `artifacts/candidate-v3.0.18/` e' stata ricostruita:
lo ZIP ora ha SHA-256 `31df249ae82e5f2527285a6eee82b581e3cba30fd35848d7e7dc6df33fd27587`
e la DLL nel pacchetto SHA-256 `77e0a316c10b4e4f6aedf324fc78604e07b8c48d600c90ec6796e923d015971c`.
La matrice completa qui sotto **non certifica questa nuova DLL**.
I runner usano copie dei modelli `examples/atomic/models/` e verificano gli
hash degli input originali. Nessun modello di progetto aperto e' stato usato.

## Matrice completa Genera/Applica

| Prodotto | Sessioni isolate contemporanee | Casi | Esito |
| --- | ---: | ---: | --- |
| SAP2000 27.1.0 | 3 × 12 | nTEST001–036 | **36/36 PASS** |
| CSiBridge 27.1.0 | 3 × 12 | nTEST001–036 | **36/36 PASS** |

Sei processi ospite in parallelo; per ciascun prodotto **34 inserimenti FAST
con commit diretto e 2 casi Standard espliciti** (`nTEST003`, `nTEST035`),
senza fallback. Riletti sommari, tracciati JSON del backend, screenshot delle
impostazioni e modelli salvati/riaperti. Seconda verifica `-VerifyOnly` PASS.
Risultati: `%TEMP%\opencode\meshing318-six-host-full-apply-20261109\`.

## Undo mirato

Una prova per ciascuna delle tre sessioni di ogni prodotto, con sei nuove
istanze isolate simultanee: `nTEST001`, `nTEST013`, `nTEST025` per SAP2000 e
gli stessi tre casi per CSiBridge. **SAP 3/3 PASS, Bridge 3/3 PASS**; tracciati
`disk-undo-phase` confermano la riapertura del file pre-mesh e la verifica
delle aree originali. Non e' stata ripetuta l'ondata Undo su tutti i 72 casi.
Risultati: `%TEMP%\opencode\meshing318-six-host-targeted-undo-20261109\`.

## Nuova opzione diagnostica e controlli di pacchetto

- Test UI senza modello: default controllo acceso/5%, preferenza persistita
  spento/12,5%, Ripristina default acceso/5%: PASS. Help IT/EN e screenshot
  della nuova scheda: PASS. Suite backend Python: 36 PASS, 1 skip.
- Sui contorni diagnostici di Cernavoda, copia SDB temporanea con 878 nodi
  pertinenti e passo 600 mm: controllo **acceso** (soglia 30 mm, 2 coppie
  segnalate) e **spento** (nessun avviso preliminare), entrambi PASS. In
  entrambi i casi il triangolo quasi allineato viene respinto dal backend
  **prima** di FAST; nessun file di import FAST. Non e' una prova di successo
  della mesh di Cernavoda. Risultati:
  `%TEMP%\opencode\meshing318-cernavoda-preflight-on-off-20261109\`.
- La prima candidata aveva dieci asset e checksum; verifica pacchetto e
  installazione temporanea PASS. L'installazione locale coincideva con la DLL
  del primo ZIP.

I log stderr di alcuni worker CSI riportano solo messaggi facoltativi su file
`.Y_` assenti; i modelli e i sommari di ogni caso sono PASS e non restano
processi SAP2000/CSiBridge dopo le prove.

## Ricontrollo della candidata ricostruita (9 novembre 2026)

`tools/VerifyMESHingV318ReleaseAssets.py`: dieci asset, checksum, ZIP
installabile e ZIP sorgenti corrispondenti PASS.
La nuova matrice sulla DLL del pacchetto ricostruito e' stata interrotta dal
timeout esterno di 20 minuti. I casi mancanti sono stati ripresi in sessioni
isolate: sommari e log complessivi verificati, **SAP2000 36/36 PASS e
CSiBridge 36/36 PASS** sulla stessa DLL del pacchetto finale. La verifica e'
composta da piu' sessioni e non va descritta come una seconda esecuzione
integrale con sei host simultanei. Tracce in
`%TEMP%\opencode\meshing318-release-final-20261109\`,
`%TEMP%\opencode\meshing318-final-sap-missing-group-{1,2,3}\` e
`%TEMP%\opencode\meshing318-final-bridge-missing\`.
