"""Bring the two packaged guides up to date for MESHing 3.0.7."""
from pathlib import Path

root = Path(__file__).resolve().parent.parent / 'docs' / 'help-gmsh-v2'
changes = {
    'index-it.html': [
        ('Il percorso FAST tabellare è automatico dove supportato; altrove resta il percorso sicuro tramite API degli oggetti.',
         'FAST verifica le tabelle prima di modificare il modello e non ripiega automaticamente sull’inserimento Standard; su SAP2000/CSiBridge 27.1 le righe di continuazione poligonale usano CSV verificato.'),
        ('In SAP2000 con tabelle FAST compatibili nodi e shell hanno subito nomi numerici definitivi e le aree originali vengono eliminate durante Genera. CSiBridge e gli schemi tabellari non supportati mantengono i percorsi di inserimento e ripristino compatibili, ma concludono ugualmente la mesh a Genera.',
         'In SAP2000 e CSiBridge con tabelle FAST compatibili nodi e shell hanno subito nomi numerici definitivi e le aree originali vengono eliminate durante Genera. Se la verifica delle tabelle FAST fallisce, Genera si ferma prima dell’inserimento senza cambiare metodo.'),
    ],
    'index-en.html': [
        ('FAST table insertion is used automatically where supported, with the existing safe object-API fallback.',
         'FAST checks the tables before modifying the model and does not silently switch to Standard insertion; verified CSV imports preserve polygon continuation rows in SAP2000/CSiBridge 27.1.'),
        ('In SAP2000 with supported FAST tables, joints and shells receive their final numeric names and source areas are removed during Generate. CSiBridge and unsupported table schemas retain their compatible insertion/rollback paths but likewise complete the mesh at Generate.',
         'In SAP2000 and CSiBridge with supported FAST tables, joints and shells receive their final numeric names and source areas are removed during Generate. If the FAST table check fails, Generate stops before insertion without changing methods.'),
    ],
}
for filename, replacements in changes.items():
    page = root / filename
    text = page.read_text(encoding='utf-8')
    if text.count('3.0.6') != 4 or '3.0.7' in text:
        raise ValueError(f'Unexpected previous guide version: {page}')
    for old, new in replacements:
        if text.count(old) != 1:
            raise ValueError(f'Expected one guide paragraph in {page}: {old}')
        text = text.replace(old, new)
    page.write_text(text.replace('3.0.6', '3.0.7'), encoding='utf-8')
    print(page)
