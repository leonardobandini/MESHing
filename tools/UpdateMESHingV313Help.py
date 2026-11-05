"""Correct the bilingual long-line O-grid guide for the 3.0.13 candidate."""
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "docs" / "help-gmsh-v2"
corrections = {
    "it": (
        "il numero effettivo può crescere se la dimensione massima della mesh lo richiede anche sul bordo esterno.",
        "il numero effettivo cresce solo se necessario sul foro o per allineare i bordi condivisi. I lati esterni dell'O-grid possono superare la dimensione nominale della mesh.",
    ),
    "en": (
        "the effective count can grow when the maximum mesh size requires shorter edges, including at the outer O-grid boundary.",
        "the effective count grows only when required on the hole or to align shared area boundaries. Outer O-grid edges can exceed the nominal mesh size.",
    ),
}
for language, (old, new) in corrections.items():
    page = root / f"index-{language}.html"
    contents = page.read_text(encoding="utf-8")
    if contents.count("3.0.12") < 4 or "3.0.13" in contents or contents.count(old) != 1:
        raise ValueError(f"Unexpected help content: {page}")
    page.write_text(contents.replace("3.0.12", "3.0.13").replace(old, new), encoding="utf-8")
