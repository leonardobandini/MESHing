"""Rebrand only the current bilingual help; preserve historical FEMesh screenshots."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HELP = ROOT / "docs" / "help-gmsh-v2"
LOGO = HELP / "assets" / "LogoMESHing.png"


def main():
    if not LOGO.is_file():
        raise FileNotFoundError(LOGO)
    for language in ("it", "en"):
        path = HELP / f"index-{language}.html"
        original = path.read_text(encoding="utf-8")
        if original.count("FEMesh") != 25 or "assets/LogoMESHing.png" in original:
            raise ValueError(f"Unexpected source help: {path.name}")
        historic = "FEMesh 2.0.61"
        if original.count(historic) != 1:
            raise ValueError(f"Historical interface label missing: {path.name}")
        text = original.replace(historic, "__HISTORIC_FEMESH_2_0_61__")
        text = text.replace("FEMeshSetup.exe", "MESHingSetup.exe")
        text = text.replace("install_plugin.cmd", "install_MESHing.cmd")
        text = text.replace(".FEMesher-backup-0001", ".MESHing-backup-0001")
        text = text.replace("FEMesh", "MESHing")
        text = text.replace("__HISTORIC_FEMESH_2_0_61__", historic)
        text = text.replace("2.0.68", "3.0.0")
        if language == "it":
            text = text.replace(
                "Le sei schermate sono acquisizioni dirette della finestra FEMesh 2.0.61,",
                "Le sei schermate storiche, che non rappresentano la UI di MESHing 3.0.0, "
                "sono acquisizioni dirette della finestra FEMesh 2.0.61,",
            )
            text = text.replace("25 ottobre 2026</small>", "in preparazione</small>")
            alt = "Logo MESHing"
        else:
            text = text.replace(
                "The six images were captured directly from the FEMesh 2.0.61 window:",
                "The six historical images do not show the MESHing 3.0.0 UI; "
                "they were captured directly from the FEMesh 2.0.61 window:",
            )
            text = text.replace("October 25, 2026</small>", "in preparation</small>")
            alt = "MESHing logo"
        marker = '<div class="hero">'
        if text.count(marker) != 1:
            raise ValueError(f"Current help has no unique hero: {path.name}")
        image = f'<img class="meshing-brand" src="assets/LogoMESHing.png" alt="{alt}">'
        text = text.replace(marker, marker + image, 1)
        if text.count(historic) != 1 or text.count("FEMesh") != 1 or text.count(image) != 1:
            raise ValueError(f"An unexpected historical or current name survived: {path.name}")
        path.write_text(text, encoding="utf-8")
        print(f"MESHING_HELP_BRANDING={path.name} HISTORY=FEMesh_2.0.61 LOGO={LOGO.name}")


if __name__ == "__main__":
    main()
