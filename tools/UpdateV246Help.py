"""Update the two one-line HTML help pages without touching archived examples."""
from pathlib import Path


help_root = Path(__file__).resolve().parents[1] / 'docs' / 'help-gmsh-v2'
for language in ('en', 'it'):
    path = help_root / f'index-{language}.html'
    text = path.read_text(encoding='utf-8')
    replacements = [
        ('Release · 2.0.45', 'Release · 2.0.46'),
        ('<strong>2.0.45</strong>', '<strong>2.0.46</strong>'),
    ]
    if language == 'en':
        replacements += [
            ('which remains unchanged in 2.0.45', 'which remains visually unchanged in 2.0.46'),
            ('Published October 14, 2026', 'Published October 15, 2026'),
            ('There is no manual structured-surface or sizing-field configuration section.</p></section>\n<section id="aggiuntive">',
             'There is no manual structured-surface or sizing-field configuration section.</p>'
             '<p><strong>Gmsh Full-Quad and BAMG:</strong> Full-Quad Simple/Blossom '
             'use even intervals on temporary Gmsh area-mesh guide curves; existing SAP2000 '
             'frame objects are neither cut nor renamed. Immutable selected edges are '
             'respected. If the chosen algorithm (notably BAMG on heavily constrained '
             'geometry) generates a nearly collinear triangle, the mesh is rejected '
             'before import with a short diagnostic. Select a different algorithm '
             '(for example 8 — Frontal-Delaunay for Quads) and Generate again; '
             'the Plug-in does not silently change an explicitly selected algorithm.</p>'
             '</section>\n<section id="aggiuntive">'),
        ]
    else:
        replacements += [
            ('rimasta invariata nella 2.0.45', 'visivamente invariata nella 2.0.46'),
            ('Pubblicata il 14 ottobre 2026', 'Pubblicata il 15 ottobre 2026'),
            ('Non esiste una sezione per l\'impostazione manuale di superfici strutturate o di campi dimensionali.</p></section>\n<section id="aggiuntive">',
             'Non esiste una sezione per l\'impostazione manuale di superfici strutturate o di campi dimensionali.</p>'
             '<p><strong>Full-Quad Gmsh e BAMG:</strong> Simple/Blossom Full-Quad '
             'usano intervalli pari sulle curve-guida temporanee della mesh delle aree '
             'in Gmsh; gli oggetti frame SAP2000 non vengono tagliati né rinominati. '
             'I bordi selezionati immutabili sono rispettati. Se l’algoritmo scelto '
             '(in particolare BAMG su geometrie molto vincolate) genera un triangolo '
             'con nodi quasi allineati, la mesh viene rifiutata prima dell’import '
             'con un avviso breve. Selezionare un altro algoritmo (per esempio '
             '8 — Frontal-Delaunay for Quads) e premere nuovamente Genera: '
             'l’algoritmo esplicitamente scelto non viene cambiato di nascosto.</p>'
             '</section>\n<section id="aggiuntive">'),
        ]
    for before, after in replacements:
        if text.count(before) != 1:
            raise ValueError(f'{path.name}: expected exactly one occurrence of {before!r}')
        text = text.replace(before, after)
    path.write_text(text, encoding='utf-8')
    print('UPDATED_HELP=' + str(path))
