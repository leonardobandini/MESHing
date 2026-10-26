"""Rebrand the two minified current V2 help pages; leave historical pages intact."""
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
HELP = ROOT / "docs" / "help-gmsh-v2"

for language in ("it", "en"):
    path = HELP / f"index-{language}.html"
    text = path.read_text(encoding="utf-8")
    replacements = {
        "SAP2000 Quad Transition Mesh": "FEMesh",
        "SAP2000 Quad<br>Transition Mesh": "FEMesh",
        "SAP2000 Quad Mesher": "FEMesh",
        "Local build · 2.0.54": "Release · 2.0.54",
        "Release · 2.0.53": "Release · 2.0.54",
        "Local build: <strong>2.0.54</strong> · " +
        ("21 ottobre 2026" if language == "it" else "October 21, 2026"):
            "Release: <strong>2.0.54</strong> · " +
            ("21 ottobre 2026" if language == "it" else "October 21, 2026"),
        "Release: <strong>2.0.53</strong> · " +
        ("20 ottobre 2026" if language == "it" else "October 20, 2026"):
            "Release: <strong>2.0.54</strong> · " +
            ("21 ottobre 2026" if language == "it" else "October 21, 2026"),
    }
    for before, after in replacements.items():
        if before not in text:
            if before.startswith("SAP2000 Quad") or after in text:
                continue
            raise ValueError(f"{path.name}: missing original or updated label {before!r}")
        text = text.replace(before, after)
    if language == "it":
        extra = {
            "Guida tecnica al Plug-in per la suddivisione di aree in SAP2000 con Gmsh":
                "Guida tecnica FEMesh per la suddivisione di aree in SAP2000 e CSiBridge con Gmsh",
            "<section id=\"introduzione\"><h2>Introduzione</h2>":
                "<section id=\"introduzione\"><h2>Introduzione</h2><p>FEMesh funziona con SAP2000 27 e CSiBridge 27: i comandi, la mesh e lo snapshot sono stati verificati su copie SDB/BDB. Gli esempi illustrati qui sono stati acquisiti originariamente in SAP2000 e conservano le schermate storiche.</p>",
            "Aprire un modello SAP2000.": "Aprire un modello SAP2000 o CSiBridge.",
            "ultimo SDB salvato": "ultimo file SDB/BDB salvato",
            "ultimo file SDB salvato": "ultimo file SDB/BDB salvato",
        }
    else:
        extra = {
            "Technical guide to the Plug-in for meshing SAP2000 areas with Gmsh":
                "FEMesh technical guide for meshing SAP2000 and CSiBridge areas with Gmsh",
            "<section id=\"introduzione\"><h2>Introduction</h2>":
                "<section id=\"introduzione\"><h2>Introduction</h2><p>FEMesh supports SAP2000 27 and CSiBridge 27: meshing and snapshot restoration have been verified on disposable SDB/BDB models. The illustrated examples were originally captured in SAP2000 and retain their historical screenshots.</p>",
            "Open a SAP2000 model.": "Open a SAP2000 or CSiBridge model.",
            "last saved SDB": "last saved SDB or BDB",
        }
    for before, after in extra.items():
        if after not in text and before in text:
            text = text.replace(before, after)
    text = text.replace(" ETABS non è attualmente supportato e l'installer non registra alcuna voce nel suo menu.", "")
    text = text.replace(" ETABS is not supported and the installer does not register a menu entry there.", "")
    if "<small>Release · 2.0.54</small>" not in text or "Tools → FEMesh" not in text:
        raise ValueError(f"{path.name}: incomplete current guide branding")
    path.write_text(text, encoding="utf-8")
    print(f"FEMESH_HELP_BRAND={path.name} PASS")
