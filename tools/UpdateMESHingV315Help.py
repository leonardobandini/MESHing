"""Bump the bilingual long-line offline help for the local 3.0.15 candidate."""
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "docs" / "help-gmsh-v2"
for language in ("it", "en"):
    page = root / f"index-{language}.html"
    text = page.read_text(encoding="utf-8")
    if text.count("3.0.14") < 4 or "3.0.15" in text:
        raise ValueError(f"Unexpected help version: {page}")
    page.write_text(text.replace("3.0.14", "3.0.15"), encoding="utf-8")
