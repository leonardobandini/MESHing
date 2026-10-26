"""Update the bilingual FEMesh guide to the 2.0.61 captures."""
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1] / "docs" / "help-gmsh-v2"


def main() -> None:
    for lang in ("it", "en"):
        path = ROOT / f"index-{lang}.html"
        data = path.read_bytes()
        old_label = ("Versione FEMesh · 2.0.60" if lang == "it" else
                     "FEMesh version · 2.0.60").encode("utf-8")
        new_label = old_label.replace(b"2.0.60", b"2.0.61")
        changes = [(old_label, new_label, 1)]
        old_footer = ("Versione: <strong>2.0.60</strong>" if lang == "it" else
                      "Version: <strong>2.0.60</strong>").encode("utf-8")
        changes.append((old_footer, old_footer.replace(b"2.0.60", b"2.0.61"), 1))
        old_intro = b"FEMesh 2.0.60," if lang == "it" else b"FEMesh 2.0.60 window:"
        changes.append((old_intro, old_intro.replace(b"2.0.60", b"2.0.61"), 1))
        for tab in ("gmsh", "additional", "transfer"):
            old = f"interface-v260-{lang}-{tab}.png".encode("ascii")
            new = f"interface-v261-{lang}-{tab}.png".encode("ascii")
            if not (ROOT / "assets" / new.decode("ascii")).is_file():
                raise FileNotFoundError(new)
            changes.append((old, new, 2))
        for old, new, expected in changes:
            if data.count(old) != expected:
                raise ValueError(f"{path.name}: expected {expected} occurrences of {old!r}")
            data = data.replace(old, new)
        path.write_bytes(data)


if __name__ == "__main__":
    main()
