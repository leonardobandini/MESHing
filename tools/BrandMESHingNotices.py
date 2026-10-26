"""Update product references in the project's notices, not third-party terms."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
path = ROOT / "THIRD_PARTY_NOTICES.md"
text = path.read_text(encoding="utf-8")
if text.count("FEMesh") != 13:
    raise ValueError("Third-party notices changed since the reviewed FEMesh release")

replacements = {
    "backend binario 2.0.68": "backend binario 3.0.0",
    "Il codice originale FEMesh è GPL-2.0-or-later.":
        "Il codice originale FEMesh, ora MESHing, è GPL-2.0-or-later.",
    "pacchetto FEMesh": "pacchetto MESHing",
    "sorgente FEMesh": "sorgente MESHing",
    "release FEMesh": "release MESHing",
    "FEMesh 2.0.68 **non include**": "MESHing 3.0.0 **non include**",
    "FEMesh non distribuisce": "MESHing non distribuisce",
    "FEMesh redistribuisce": "MESHing redistribuisce",
    "FEMesh/backend": "MESHing/backend",
    "sorgenti FEMesh corrispondenti": "sorgenti MESHing corrispondenti",
}
for before, after in replacements.items():
    if before not in text:
        raise ValueError("Expected product notice not found: " + before)
    text = text.replace(before, after)
if text.count("FEMesh") != 1 or "codice originale FEMesh, ora MESHing" not in text:
    raise ValueError("Unexpected residual commercial FEMesh name in current notices")
path.write_text(text, encoding="utf-8")
print("MESHING_NOTICES_BRAND=PASS ORIGINAL_ATTRIBUTION=FEMesh RETAINED")
