# MESHing

MESHing è un plugin open source per generare mesh di elementi shell prevalentemente quadrangolari in **SAP2000 e CSiBridge 27+**, usando Gmsh. L'installer registra **Tools → MESHing** negli host già inizializzati. Windows x64 e .NET Framework 4.8 sono necessari per il plugin; Python e Gmsh sono inclusi nello ZIP installabile.

## Distribuzione corrente

- **MESHing 3.0.0**: [pagina della release](https://github.com/leonardobandini/MESHing/releases/tag/v3.0.0) · [ZIP dell'ultima release](https://github.com/leonardobandini/MESHing/releases/latest/download/MESHing-latest.zip) · [ZIP, sorgenti e checksum](packaging/SOURCE.template.md).
- Prerequisito esterno: [Microsoft Visual C++ Redistributable x64](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist), versione **14.42.34226.3 o successiva compatibile**. L'installer, se manca, lo scarica dal sito Microsoft, verifica la firma e avvia il setup ufficiale interattivo prima di installare MESHing. Nessuna DLL runtime Visual C++ Microsoft è inclusa nello ZIP MESHing.
- Per installare MESHing 3.0.0: estrai lo ZIP e avvia **`MESHingSetup.exe`** (oppure `install_MESHing.cmd`, che apre la stessa finestra). L'installer grafico mostra licenze, sorgenti e tutte le fasi in italiano o inglese; rileva la lingua di Windows e permette di cambiarla manualmente. Prima di sostituire la versione precedente e registrarsi nei menu di SAP2000/CSiBridge, richiede in modo bloccante la chiusura delle eventuali sessioni aperte, senza chiudere o salvare modelli. Se richiesto, completa il setup Microsoft e l'eventuale riavvio di Windows; infine riapri l'host.
- [Guida MESHing in italiano](docs/help-gmsh-v2/index-it.html) · [English help](docs/help-gmsh-v2/index-en.html).
- [Build dai sorgenti](packaging/SOURCE_BUILD.md) · [licenze e termini](LICENSE-APPLICATION.md) · [componenti di terzi](THIRD_PARTY_NOTICES.md).

MESHing **3.0.0** include `SOURCE.md` con i riferimenti ai sorgenti corrispondenti della medesima release. La verifica di ogni modello ingegneristico risultante resta a carico dell'utilizzatore.

MESHing conserva l'identità tecnica del plugin della versione 2.0.68 per consentire la sostituzione dell'installazione esistente senza duplicare la registrazione COM o i menu. I riferimenti storici alla 2.0.68 restano nella sua release originale, che non viene modificata.

## Salvataggio e ripristino della mesh

Con il modello già salvato, MESHing lo blocca mentre la finestra del plugin è aperta. A **ogni Genera**, se l'opzione backup è attiva, copia prima l'SDB/BDB precedentemente salvato in un file numerato. Poi **salva sempre il modello corrente nel file attivo**, anche con backup disattivato, e acquisisce lo snapshot temporaneo per Annulla mesh. Il backup non include le modifiche rimaste in memoria prima di Genera: sono invece presenti nel successivo salvataggio automatico.

Se MESHing viene aperto su un modello nuovo mai salvato, avvisa l'utente e lascia consultabili schede e guida, ma disabilita i comandi di meshatura. Dopo il primo salvataggio in SAP2000/CSiBridge rilegge il modello e, se è selezionata un'area, rende disponibile Genera.

**Applica** mantiene la mesh nel modello in memoria senza salvare la mesh sul file: per conservarla occorre un salvataggio esplicito nell'host. **Annulla mesh** ripristina lo stato acquisito subito dopo il salvataggio di Genera, senza un nuovo salvataggio. Un successivo Genera ripete backup opzionale, salvataggio e snapshot. Per tornare allo stato su disco *precedente* a Genera occorre la copia numerata, se creata.

Il codice originale del progetto, ora distribuito come MESHing, è **GPL-2.0-or-later**; Gmsh 4.15.2 è **GPL-2.0-or-later** dei suoi titolari. Per il backend binario combinato con OpenSSL 3 (Apache-2.0) viene scelta **GPLv3** secondo le clausole «or later». Le licenze degli altri componenti e l'eccezione limitata di Gmsh restano invariate. SAP2000, CSiBridge e `CSiAPIv1.dll` non sono inclusi nel pacchetto.

MESHing utilizza **Gmsh 4.15.2** e il suo binding Python. Gmsh è copyright © **C. Geuzaine e J.-F. Remacle**, GPL-2.0-or-later con la propria eccezione limitata: [sito ufficiale Gmsh](https://gmsh.info/). Licenza e avvisi sono nel pacchetto (`licenses/GMSH-LICENSE.txt`); vedi [avvisi di terze parti](THIRD_PARTY_NOTICES.md) e il sorgente Gmsh fra gli asset della [medesima release MESHing](https://github.com/leonardobandini/MESHing/releases/tag/v3.0.0). La scelta GPLv3 del backend combinato non cambia la licenza originale di Gmsh.

Il controllo aggiornamenti nel piè di pagina verifica esclusivamente la più recente release stabile MESHing. Segnala una nuova versione senza installarla: solo il comando **Scarica** apre nel browser lo ZIP; l'installazione rimane manuale. In assenza di rete il plugin continua a funzionare.
