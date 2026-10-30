"""Refresh the transaction description in the two long-line offline help files."""

from pathlib import Path
import re

root = Path(__file__).resolve().parent.parent / "docs" / "help-gmsh-v2"

sections = {
    "it": '''<section id="transazione"><h2>Anteprima, conferma e nuova operazione</h2>
<p><strong>Ordine delle operazioni a Genera.</strong> Se il backup numerato è attivo, MESHing copia prima il file SDB/BDB dell'ultimo salvataggio (prima delle modifiche ancora in memoria) con suffisso <code>.MESHing-backup-0001</code>. Con backup attivo o disattivato, salva quindi il modello corrente nel file attivo e protegge una copia privata dello stato pre-mesh. «Non mostrare più questo avviso sul backup» riguarda solo questa scelta.</p>
<p><strong>SAP2000 con FAST tabellare:</strong> Genera inserisce nodi e shell già con nomi numerici definitivi e rimuove in blocco le aree originali. Applica conferma la mesh già presente nel modello in memoria e rilascia il punto di ripristino; non salva il modello meshed sul disco. Annulla mesh abbandona il modello modificato senza salvarlo, verifica che l'SDB pre-mesh non sia cambiato sul disco, lo riapre e ripristina la selezione. Se l'SDB è cambiato, il ripristino non lo sovrascrive e la copia privata resta disponibile. Nessun nome di area originale viene imposto a una nuova shell.</p>
<p>Nel percorso Standard e in CSiBridge le aree originali restano visibili durante l'anteprima; Applica le elimina dal modello in memoria. In tali percorsi Annulla mesh conserva lo snapshot CSV di compatibilità. <strong>Annulla generazione</strong> nella finestra di avanzamento usa invece il file SDB/BDB appena salvato in ogni modalità: la richiesta è registrata subito e il ripristino inizia appena la chiamata API in corso restituisce il controllo. Durante le operazioni il modello resta bloccato contro modifiche.</p>
<p>Applica chiede una seconda conferma indipendente: dopo «Sì» Annulla mesh non è più disponibile. «Non mostrare più questa conferma di Applica» è indipendente dall'avviso sul backup. Per conservare la mesh confermata nell'SDB/BDB occorre un salvataggio esplicito nell'host. Il backup numerato opzionale e la copia privata post-salvataggio hanno scopi diversi.</p></section>''',
    "en": '''<section id="transazione"><h2>Preview, confirmation and subsequent operations</h2>
<p><strong>Generate-time order.</strong> When numbered backup is enabled, MESHing first copies the previously saved SDB/BDB (without edits still in memory) to <code>.MESHing-backup-0001</code>. With or without backup it then saves the current model to its active file and protects a private copy of the pre-mesh state. “Do not show this backup warning again” controls only that choice.</p>
<p><strong>SAP2000 with tabular FAST:</strong> Generate inserts joints and shells with their final numeric names and deletes the original areas in one batch. Apply confirms the mesh already present in memory and releases the rollback point; it does not save the meshed model to disk. Undo mesh abandons the modified in-memory model without saving, verifies that the pre-mesh SDB on disk has not changed, reopens it and restores the selection. If that file changed, Undo does not overwrite it and retains the private copy. No new shell is forced to keep a source area's original name.</p>
<p>In Standard and CSiBridge previews the source areas remain visible until Apply removes them from the in-memory model. Undo mesh retains the compatible CSV snapshot for those preview paths. <strong>Cancel generation</strong> in the progress window instead reloads the saved SDB/BDB in every mode: the request is latched immediately and rollback starts when the current API call returns. The model stays locked against editing during the operation.</p>
<p>Apply has an independent second confirmation: after Yes, Undo mesh is no longer available. “Do not show this Apply confirmation again” is independent of the backup warning. Saving the confirmed mesh to SDB/BDB requires an explicit save in the host. The optional numbered backup and the private post-save rollback copy serve different purposes.</p></section>''',
}

for language, section in sections.items():
    path = root / f"index-{language}.html"
    text = path.read_text(encoding="utf-8")
    if "3.0.5" not in text or "3.0.4" in text:
        raise ValueError(f"Wrong version before editing {path}")
    text, count = re.subn(r'<section id="transazione">.*?</section>', section, text,
                          count=1, flags=re.DOTALL)
    if count != 1:
        raise ValueError(f"Missing transaction section in {path}")
    if language == "it":
        before = "acquisisce lo snapshot per <strong>Annulla mesh</strong> e genera la mesh."
        after = "protegge lo stato pre-mesh e genera la mesh."
        old_undo = "Annulla mesh ripristina lo snapshot acquisito dopo il salvataggio di Genera e prima di Applica, senza salvare nuovamente il file."
        new_undo = "Annulla mesh riapre l'SDB pre-mesh con FAST in SAP2000; negli altri casi usa lo snapshot CSV. Annulla generazione ricarica il file pre-mesh in ogni modalità."
    else:
        before = "captures the snapshot for <strong>Undo mesh</strong> and generates the mesh."
        after = "protects the pre-mesh state and generates the mesh."
        old_undo = "Undo mesh restores the snapshot captured after the Generate-time save and before Apply, without saving the file again."
        new_undo = "Undo mesh reopens the pre-mesh SDB with SAP2000 FAST; other preview paths retain the CSV snapshot. Cancel generation reloads the pre-mesh file in every mode."
    for old, new in ((before, after), (old_undo, new_undo)):
        if text.count(old) != 1:
            raise ValueError(f"Unexpected occurrence of {old!r} in {path}")
        text = text.replace(old, new)
    path.write_text(text, encoding="utf-8")
