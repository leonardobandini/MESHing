"""Synchronize the bilingual long-line help with the local 3.0.12 candidate."""
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "docs" / "help-gmsh-v2"
updates = {
    "it": (
        '<section id="aggiuntive"><h2>Opzioni aggiuntive</h2>',
        '<p><strong>Fori circolari e O-grid (3.0.12).</strong> Il numero minimo di corde è 12 per impostazione predefinita (intero ≥ 4); il numero effettivo può crescere se la dimensione massima della mesh lo richiede anche sul bordo esterno. La larghezza radiale O-grid, misurata dal bordo del foro, si immette in diametri del foro (default 0,5). Il numero di strati radiali si calcola automaticamente dividendo la larghezza effettiva per la corda effettiva e arrotondando per eccesso. Ostacoli e vincoli geometrici possono ridurre la larghezza disponibile.</p>',
    ),
    "en": (
        '<section id="aggiuntive"><h2>Additional options</h2>',
        '<p><strong>Circular holes and O-grid (3.0.12).</strong> The default minimum chord count is 12 (integer ≥ 4); the effective count can grow when the maximum mesh size requires shorter edges, including at the outer O-grid boundary. Enter radial O-grid width from the hole edge in hole diameters (default 0.5). Radial shell layers are calculated automatically by dividing the effective width by the actual chord length and rounding up. Nearby geometry and constraints can limit the available width.</p>',
    ),
}
for language, (marker, detail) in updates.items():
    page = root / f"index-{language}.html"
    content = page.read_text(encoding="utf-8")
    if content.count("3.0.7") < 3 or "3.0.12" in content or content.count(marker) != 1:
        raise ValueError(f"Unexpected candidate help content: {page}")
    updated = content.replace("3.0.7", "3.0.12").replace(marker, marker + detail)
    page.write_text(updated, encoding="utf-8")
