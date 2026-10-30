"""Update the release label in the two long-line offline help pages."""
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "docs" / "help-gmsh-v2"
for language in ("it", "en"):
    page = root / f"index-{language}.html"
    text = page.read_text(encoding="utf-8")
    if text.count("3.0.4") != 3 or "3.0.5" in text:
        raise ValueError(f"Unexpected help version markers: {page}")
    page.write_text(text.replace("3.0.4", "3.0.5"), encoding="utf-8")
