"""Update the current bilingual guide to match the 2.0.42 screenshots and UI."""
from pathlib import Path

root = Path(__file__).resolve().parents[1] / 'docs' / 'help-gmsh-v2'


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise ValueError(f'Expected one guide reference, found {text.count(old)}: {old[:75]}')
    return text.replace(old, new)


for language in ('en', 'it'):
    path = root / f'index-{language}.html'
    text = path.read_text(encoding='utf-8')
    text = replace_once(text, 'Release · 2.0.41', 'Release · 2.0.42')
    if language == 'en':
        text = replace_once(text,
            'The bottom bar displays status, progress and the number of created elements.',
            'During generation a separate window shows phase progress and a persistent area indicator: '
            'the current area X/N for sequential work, or areas 1–N/N together during conforming Gmsh '
            'meshing and bulk SAP import. The main window shows completion status and created elements.')
        text = replace_once(text,
            '<section id="transazione"><h2>Preview, Apply and subsequent operations</h2>',
            '<section id="transazione"><h2>Preview, Apply and subsequent operations</h2><p>Closing '
            'the options window with X before Apply performs the same verified Undo as the button, '
            'restoring the original objects and selection. Closing after Apply keeps the mesh.</p>')
        text = replace_once(text, 'direct captures of the version 2.0.41 dialog',
            'direct captures of the version 2.0.42 dialog')
        text = replace_once(text, '2 generation algorithm and its settings; 3 triangle recombination; 4 mesh optimization.',
            '2 generation algorithm; 3 smoothing steps; 4 triangle recombination; 5 mesh optimization.')
        text = replace_once(text, 'Latest release: <strong>2.0.41</strong>',
            'Latest release: <strong>2.0.42</strong>')
    else:
        text = replace_once(text,
            'La barra inferiore riporta stato, avanzamento e numero di elementi creati.',
            'Durante la generazione una finestra dedicata mostra avanzamento della fase e indicazione '
            'persistente delle aree: area X/N nelle operazioni sequenziali, oppure aree 1–N/N insieme '
            'per Gmsh e import SAP in blocco. La finestra principale mostra lo stato conclusivo e gli elementi creati.')
        text = replace_once(text,
            '<section id="transazione"><h2>Anteprima, conferma e nuova operazione</h2>',
            '<section id="transazione"><h2>Anteprima, conferma e nuova operazione</h2><p>Chiudere '
            'la finestra delle opzioni con la X prima di Applica esegue lo stesso Undo verificato del '
            'pulsante, ripristinando oggetti e selezione originali. Chiuderla dopo Applica conserva la mesh.</p>')
        text = replace_once(text, 'finestra della versione 2.0.41',
            'finestra della versione 2.0.42')
        text = replace_once(text, '2 generazione e impostazioni dell\'algoritmo; 3 ricomposizione dei triangoli; 4 ottimizzazione.',
            '2 algoritmo di generazione; 3 passaggi di smoothing; 4 ricomposizione dei triangoli; 5 ottimizzazione.')
        text = replace_once(text, 'Ultima release: <strong>2.0.41</strong>',
            'Ultima release: <strong>2.0.42</strong>')
    if text.count('interface-v241-') != 8:
        raise ValueError(f'Expected four current figures in {path.name}')
    text = text.replace('interface-v241-', 'interface-v242-')
    if '2.0.41' in text:
        raise ValueError(f'Stale release version in {path.name}')
    path.write_text(text, encoding='utf-8')
    print(f'HELP_V242_UPDATED={path.name}')
