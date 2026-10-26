"""Point the local-only FEMesh 2.0.60 guide at fresh bilingual captures."""
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1] / "docs" / "help-gmsh-v2"


def main() -> None:
    for lang in ("it", "en"):
        path = ROOT / f"index-{lang}.html"
        data = path.read_bytes()
        if b"interface-v260-" in data and b"interface-v259-" not in data:
            continue
        changes = [
            (b"Versione FEMesh \xc2\xb7 2.0.59" if lang == "it" else
             b"FEMesh version \xc2\xb7 2.0.59",
             b"Versione FEMesh \xc2\xb7 2.0.60" if lang == "it" else
             b"FEMesh version \xc2\xb7 2.0.60", 1),
            (b"Versione: <strong>2.0.59</strong>" if lang == "it" else
             b"Version: <strong>2.0.59</strong>",
             b"Versione: <strong>2.0.60</strong>" if lang == "it" else
             b"Version: <strong>2.0.60</strong>", 1),
            (b"FEMesh 2.0.59," if lang == "it" else b"FEMesh 2.0.59 window:",
             b"FEMesh 2.0.60," if lang == "it" else b"FEMesh 2.0.60 window:", 1),
        ]
        for tab in ("gmsh", "additional", "transfer"):
            old = f"interface-v259-{lang}-{tab}.png".encode("ascii")
            new = f"interface-v260-{lang}-{tab}.png".encode("ascii")
            if not (ROOT / "assets" / new.decode("ascii")).is_file():
                raise FileNotFoundError(new)
            changes.append((old, new, 2))
        for old, new, expected in changes:
            if data.count(old) != expected:
                raise ValueError(f"{path.name}: expected {expected} references to {old!r}")
            data = data.replace(old, new)
        path.write_bytes(data)


if __name__ == "__main__":
    main()
