"""Synchronize the bilingual long-line help with the local 3.0.14 candidate."""
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "docs" / "help-gmsh-v2"
changes = {
    "it": (
        "Il numero minimo di corde è 12 per impostazione predefinita (intero ≥ 4);",
        "Il numero minimo di corde è 8 per impostazione predefinita (intero ≥ 8);",
    ),
    "en": (
        "The default minimum chord count is 12 (integer ≥ 4);",
        "The default minimum chord count is 8 (integer ≥ 8);",
    ),
}
for language, (old, new) in changes.items():
    page = root / f"index-{language}.html"
    text = page.read_text(encoding="utf-8")
    if text.count("3.0.13") < 4 or "3.0.14" in text or text.count(old) != 1:
        raise ValueError(f"Unexpected help content: {page}")
    page.write_text(text.replace("3.0.13", "3.0.14").replace(old, new), encoding="utf-8")
