"""Align the current bilingual guide with the 2.0.44 topology and UI changes."""
from pathlib import Path

HELP = Path(__file__).resolve().parents[1] / 'docs' / 'help-gmsh-v2'


def once(text, old, new):
    count = text.count(old)
    if count != 1:
        raise ValueError(f'Expected one match, got {count}: {old[:90]}')
    return text.replace(old, new)


for language in ('en', 'it'):
    path = HELP / f'index-{language}.html'
    text = path.read_text(encoding='utf-8')
    text = once(text, 'Release · 2.0.43', 'Release · 2.0.44')
    if text.count('interface-v243-' + language + '-') != 6:
        raise ValueError('Expected three current tab screenshots in ' + path.name)
    text = text.replace('interface-v243-' + language + '-', 'interface-v244-' + language + '-')
    if language == 'en':
        text = once(text, 'direct captures of the version 2.0.43 dialog',
                    'direct captures of the version 2.0.44 dialog')
        text = once(text, '<tr><td>MeshAdapt Settings: Refinement Steps</td><td>Mesh.RefineSteps</td><td>10</td><td>integer &gt; 0</td>',
                    '<tr><td>MeshAdapt Settings: Refinement Steps</td><td>Mesh.RefineSteps</td><td>10</td><td>integer ≥ 0</td>')
        text = once(text, 'The quality report instead uses a <strong>fixed assessment policy, not a user setting</strong>',
                    'The plugin instead uses a <strong>fixed assessment policy, not a user setting</strong>')
        text = once(text, '<h3>Quality checks and internal settings</h3>',
            '<h3>Quality checks and internal settings</h3><p>The quality window displays only the retained '
            'metric cards. Overall verdict, summary and scale legend are not shown there; sizing and '
            'validity checks still run when generating the mesh.</p>')
        text = once(text, '<section id="trasferimento"><h2>Transfer options</h2>',
            '<section id="trasferimento"><h2>Transfer options</h2><p>If an optional property or load '
            'cannot be transferred by the SAP2000 API with return code 1, generation continues and a '
            'warning identifies the property, API and affected elements for manual review. Geometry and '
            'connectivity failures still stop the operation.</p>')
        text = once(text, '<section id="bordi"><h2>Edges with selected joints</h2>',
            '<section id="bordi"><h2>Edges with selected joints</h2><p>Selection groups can contain '
            'unrelated points or frames; those are not applied as meshing constraints. Coincident SAP2000 '
            'joint labels remain separate unless they belong to the same actual mesh topology. A '
            'connected existing edge is recognized by a continuous chain of SAP joint identities between '
            'its original endpoints, not by coordinates alone.</p>')
        text = once(text, 'Latest release: <strong>2.0.43</strong>',
                    'Latest release: <strong>2.0.44</strong>')
    else:
        text = once(text, 'finestra della versione 2.0.43', 'finestra della versione 2.0.44')
        text = once(text, '<tr><td>Impostazioni MeshAdapt: Passaggi di raffinamento</td><td>Mesh.RefineSteps</td><td>10</td><td>intero &gt; 0</td>',
                    '<tr><td>Impostazioni MeshAdapt: Passaggi di raffinamento</td><td>Mesh.RefineSteps</td><td>10</td><td>intero ≥ 0</td>')
        text = once(text, '<h3>Controlli di qualità e impostazioni interne</h3>',
            '<h3>Controlli di qualità e impostazioni interne</h3><p>La finestra Qualità mostra solo '
            'le schede delle metriche rimaste. Giudizio complessivo, riepilogo e legenda non sono '
            'visualizzati; dimensioni e validità vengono comunque verificate durante la generazione.</p>')
        text = once(text, '<section id="trasferimento"><h2>Opzioni di trasferimento</h2>',
            '<section id="trasferimento"><h2>Opzioni di trasferimento</h2><p>Se il trasferimento '
            'di una proprietà o di un carico facoltativo restituisce codice API SAP2000 1, la mesh '
            'prosegue e un avviso identifica proprietà, chiamata API ed elementi da controllare '
            'manualmente. Gli errori di geometria e connettività restano bloccanti.</p>')
        text = once(text, '<section id="bordi"><h2>Bordi con nodi selezionati</h2>',
            '<section id="bordi"><h2>Bordi con nodi selezionati</h2><p>I nodi e i frame selezionati '
            'ma estranei alle aree da suddividere non sono vincoli. Nodi SAP2000 con coordinate '
            'sovrapposte restano oggetti distinti finché non appartengono alla stessa topologia; una '
            'cucitura preesistente è riconosciuta da una catena continua dei nomi dei nodi fra i '
            'vertici originali, non dalla sola coincidenza geometrica.</p>')
        text = once(text, 'Ultima release: <strong>2.0.43</strong>',
                    'Ultima release: <strong>2.0.44</strong>')
    path.write_text(text, encoding='utf-8')
    print('HELP_V244_UPDATED=' + path.name)
