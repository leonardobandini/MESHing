"""Refresh the current bilingual V2 guide for the three-tab 2.0.43 interface."""
import re
from pathlib import Path

root = Path(__file__).resolve().parents[1] / 'docs' / 'help-gmsh-v2'


def once(text, old, new):
    count = text.count(old)
    if count != 1:
        raise ValueError(f'Expected one occurrence, found {count}: {old[:90]}')
    return text.replace(old, new)


for language in ('en', 'it'):
    path = root / f'index-{language}.html'
    text = path.read_text(encoding='utf-8')
    text = once(text, 'Release · 2.0.42', 'Release · 2.0.43')
    text = once(text, f'<a href="#json">{"Numeric options" if language == "en" else "Opzioni numeriche"}</a>', '')
    section = re.compile(r'<section id="json">.*?</section>', re.DOTALL)
    text, count = section.subn('', text)
    if count != 1:
        raise ValueError(f'Expected one debug JSON section in {path.name}')
    figure = re.compile(r'<figure><a href="assets/interface-v242-' + language +
                        r'-json\.png".*?</figure>', re.DOTALL)
    text, count = figure.subn('', text)
    if count != 1:
        raise ValueError(f'Expected one current JSON illustration in {path.name}')
    if text.count('interface-v242-' + language + '-') != 6:
        raise ValueError(f'Expected three current illustrations in {path.name}')
    text = text.replace('interface-v242-' + language + '-', 'interface-v243-' + language + '-')
    if language == 'en':
        text = once(text, 'in the four tabs', 'in the three tabs')
        text = once(text, 'across all four tabs', 'across all three tabs')
        text = once(text, 'direct captures of the version 2.0.42 dialog',
                    'direct captures of the version 2.0.43 dialog')
        text = once(text, 'use the four images in this section', 'use the three images in this section')
        text = once(text,
            'Closing the options window with X before Apply performs the same verified Undo as the button, restoring the original objects and selection. Closing after Apply keeps the mesh.',
            'Closing an unapplied preview with X requires a choice between Undo mesh and Apply mesh. '
            'A status window stays open while the selected operation runs, then the plugin closes. '
            'Closing before Generate or after Apply requires no confirmation.')
        text = once(text, 'use the effective-settings pane to inspect what is sent to Gmsh.',
            'the effective JSON can still be inspected in diagnostic traces; previously saved hidden overrides are ignored.')
        text = once(text, '<h2>Additional Options</h2>', '<h2>Additional options</h2>')
        text = text.replace('Additional Options', 'Additional options')
        text = text.replace('Transfer Options', 'Transfer options')
        text = once(text, 'Favor regular grid-aligned mesh (when possible)',
            'Prefer regular (grid-aligned) mesh when possible')
        text = once(text, 'Enforce exact symmetry about the chosen axis/axes',
            'Enforce mesh symmetry about the following axes')
        text = once(text, 'Cut circular holes at grouped SAP2000 joints',
            'Create circular holes around this group of joints')
        text = once(text, 'Cut openings bounded by grouped frames or shell edges',
            'Create openings around the following group of frames and/or shells edges')
        text = once(text, 'FAST — bulk insertion using SAP2000 editable tables',
            'Use interactive database to generate elements in SAP2000 (fast mode)')
        text = once(text, 'Create a numbered copy of the saved model before meshing',
            'Create a backup copy of the model before each mesh generation')
        text = once(text, 'Latest release: <strong>2.0.42</strong>',
            'Latest release: <strong>2.0.43</strong>')
        row = '<tr><td>Provide a supplemental numeric option</td><td>Under Gmsh JSON: <code>Mesh.RandomFactor = 1e-8</code>.</td><td>Check the effective-settings pane to confirm the option is passed to the worker without duplicating an existing control.</td></tr>'
    else:
        text = once(text, 'nelle quattro schede', 'nelle tre schede')
        text = once(text, 'delle quattro schede', 'delle tre schede')
        text = once(text, 'finestra della versione 2.0.42', 'finestra della versione 2.0.43')
        text = once(text, 'alle quattro figure', 'alle tre figure')
        text = once(text,
            'Chiudere la finestra delle opzioni con la X prima di Applica esegue lo stesso Undo verificato del pulsante, ripristinando oggetti e selezione originali. Chiuderla dopo Applica conserva la mesh.',
            'Chiudendo con la X un\'anteprima non applicata bisogna scegliere fra Annulla mesh e Applica mesh. '
            'Una finestra di stato resta visibile durante l\'operazione scelta, poi il plugin si chiude. '
            'Prima di Genera e dopo Applica non è richiesta conferma.')
        text = once(text, 'il riquadro JSON serve a verificare le impostazioni effettive.',
            'il JSON effettivo rimane consultabile nei tracciati diagnostici; gli override nascosti salvati in passato sono ignorati.')
        text = once(text, 'FAST — inserimento massivo tramite tabelle SAP2000',
            'Usa il database interattivo per generare elementi in SAP2000 (modalità veloce)')
        text = once(text, 'Crea copia numerata del modello salvato prima della mesh',
            'Crea una copia di backup del modello prima di ogni generazione mesh')
        text = once(text, 'Ultima release: <strong>2.0.42</strong>',
            'Ultima release: <strong>2.0.43</strong>')
        row = '<tr><td>Specificare un\'opzione numerica supplementare</td><td>Nella scheda JSON Gmsh: <code>Mesh.RandomFactor = 1e-8</code>.</td><td>Controllare nella vista delle impostazioni effettive che l\'opzione sia trasmessa al processo, senza tentare di duplicare i controlli esistenti.</td></tr>'
    text = once(text, row, '')
    if 'interface-v242-' + language in text:
        raise ValueError(f'Stale current screenshot in {path.name}')
    path.write_text(text, encoding='utf-8')
    print('HELP_V243_UPDATED=' + path.name)
