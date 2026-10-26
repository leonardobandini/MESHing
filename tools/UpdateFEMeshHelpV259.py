"""Refresh the two current guides after capturing interface-v259-* from FEMesh."""
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1] / "docs" / "help-gmsh-v2"


def replace_once(text: str, old: str, new: str, name: str) -> str:
    count = text.count(old)
    if count != 1:
        raise ValueError(f"{name}: expected one {old!r}, found {count}")
    return text.replace(old, new)


def main() -> None:
    for language in ("it", "en"):
        name = f"index-{language}.html"
        path = ROOT / name
        text = path.read_text(encoding="utf-8")
        text = replace_once(text, "Local build · 2.0.58",
                            ("Versione FEMesh" if language == "it" else "FEMesh version") + " · 2.0.59", name)
        text = replace_once(text, "Local build: <strong>2.0.58</strong>" if language == "en"
                            else "Build locale: <strong>2.0.58</strong>",
                            ("Versione" if language == "it" else "Version") + ": <strong>2.0.59</strong>", name)
        intro = re.compile(r'(<section id="interfaccia"><h2>[^<]+</h2>)<p>.*?</p>(<div class="ui-grid">)')
        if len(intro.findall(text)) != 1:
            raise ValueError(f"{name}: missing current interface introduction")
        detail = ("Le sei schermate sono acquisizioni dirette della finestra FEMesh 2.0.59, "
                  "tre per ciascuna lingua, con una selezione dimostrativa di un'area. "
                  "I numeri identificano i controlli e non rappresentano una mesh calcolata. "
                  "Selezionare una figura per aprirla alla risoluzione originale."
                  if language == "it" else
                  "The six images were captured directly from the FEMesh 2.0.59 window: "
                  "three in each language, using a demonstration selection of one area. "
                  "Numbered callouts identify controls, not a computed mesh. "
                  "Select an image to view it at its original resolution.")
        text = intro.sub(lambda match: match.group(1) + "<p>" + detail + "</p>" + match.group(2), text)
        for tab in ("gmsh", "additional", "transfer"):
            old = f"interface-v244-{language}-{tab}.png"
            new = f"interface-v259-{language}-{tab}.png"
            if text.count(old) != 2 or not (ROOT / "assets" / new).is_file():
                raise ValueError(f"{name}: missing screenshot pair or capture: {old}")
            text = text.replace(old, new)
        # Former example captures show an obsolete interface, including a JSON tab.
        # Preserve numerical results, current backend JSON, and source/result geometry.
        pattern = re.compile(
            r'<figure><a href="assets/new-ui-[^"]+-(?:options|additional|transfer|symmetry|json)\.png">.*?</figure>',
            re.DOTALL)
        text, removed = pattern.subn("", text)
        if removed < 23:
            raise ValueError(f"{name}: expected at least 23 obsolete example UI captures, found {removed}")
        text, old_links = re.subn(
            r'<p><a href="assets/new-ui-[^"]+-json\.png">.*?</p>', "", text, flags=re.DOTALL)
        if old_links < 23:
            raise ValueError(f"{name}: expected archived JSON/transfer screenshot links, found {old_links}")
        if language == "it":
            text = replace_once(text,
                "Ogni caso collega screenshot delle opzioni realmente selezionate, SVG della geometria iniziale, SVG della mesh risultante e JSON backend.",
                "Ogni caso collega gli SVG della geometria iniziale e della mesh risultante e il JSON backend; "
                "la disposizione attuale dei controlli è mostrata sopra nelle sei schermate aggiornate.", name)
            text = text.replace("Gli esempi illustrati qui sono stati acquisiti originariamente in SAP2000 e conservano le schermate storiche.",
                                "Gli schemi dei modelli provengono dall'archivio SDB; le prove BDB sono verificate separatamente.")
            text = text.replace("copie isolate di modelli SAP2000", "copie isolate dei modelli SDB")
            text = text.replace("Build locale 2.0.47: ", "")
        else:
            text = replace_once(text,
                "Each entry links the actual configured interface image, SVG of the original geometry, SVG of the resulting mesh, and the backend JSON.",
                "Each entry links the original and resulting geometry and the backend JSON; "
                "the current control layout appears in the six updated screenshots above.", name)
            text = text.replace("The illustrated examples were originally captured in SAP2000 and retain their historical screenshots.",
                                "The model diagrams come from the SDB catalog; BDB trials were verified separately.")
            text = text.replace("isolated SAP2000 model copies", "isolated SDB model copies")
            text = text.replace("Local 2.0.47: ", "")
        # A historical beta check belongs to the first host, not to both hosts.
        text = text.replace("beta SAP2000 27.2.0", "beta 27.2.0 del primo host")
        text = text.replace("SAP2000 27.2.0 beta", "27.2.0 beta of the first host")
        for old in ("SAP2000 and CSiBridge", "SAP2000 or CSiBridge", "SAP2000 e CSiBridge",
                    "SAP2000 o CSiBridge", "SAP2000 and/or CSiBridge"):
            text = text.replace(old, "SAP2000/CSiBridge")
        text = re.sub(r"SAP2000(?!/CSiBridge)", "SAP2000/CSiBridge", text)
        if re.search(r"\bETABS\b|interface-v244-|new-ui-[^\"<> ]+-(?:options|additional|transfer|symmetry|json)\.png",
                     text, re.IGNORECASE):
            raise ValueError(f"{name}: current help still contains an excluded host or old UI image")
        path.write_text(text, encoding="utf-8", newline="")
        print(f"FEMESH_CURRENT_HELP={name} REPLACED_EXAMPLE_UI={removed} OLD_LINKS={old_links}")


def polish() -> None:
    pairs = {
        "it": (
            ("Le immagini degli esempi archiviate più sotto mostrano le esecuzioni precedenti con la preferenza OFF.",
             "I diagrammi SVG più sotto documentano le precedenti esecuzioni con la preferenza OFF."),
            ("I conteggi sopra sono verifiche separate: non sostituiscono screenshot, qualità e JSON delle 23 esecuzioni archiviate.",
             "Questi conteggi sono prove distinte dagli SVG, dai dati di qualità e dai JSON delle 23 esecuzioni SDB archiviate."),
            ("I risultati seguenti sono stati <strong>rigenerati con la nuova UI su copie isolate dei modelli SDB</strong>.",
             "I risultati seguenti provengono da <strong>esecuzioni V2 precedenti su copie isolate dei modelli SDB</strong>."),
            ("Conteggi ed esito qualità derivano dalla stessa esecuzione SAP.",
             "Conteggi ed esito qualità derivano dalla stessa esecuzione SDB; le prove BDB sono separate."),
        ),
        "en": (
            ("Archived images further down show earlier executions with the preference OFF.",
             "The SVG diagrams further down document earlier executions with the preference OFF."),
            ("These counts are separate checks and do not replace the screenshots, quality measurements and JSON of the 23 archived executions.",
             "These counts are separate from the SVG diagrams, quality measurements and JSON of the 23 archived SDB executions."),
            ("The following results were <strong>rerun with the New UI on isolated SDB model copies</strong>.",
             "The following results come from <strong>earlier V2 executions on isolated SDB model copies</strong>."),
            ("Counts and quality status come from the same completed SAP run.",
             "Counts and quality status come from the same completed SDB run; BDB trials are separate."),
        ),
    }
    for language, replacements in pairs.items():
        path = ROOT / f"index-{language}.html"
        text = path.read_text(encoding="utf-8")
        for old, new in replacements:
            text = replace_once(text, old, new, path.name)
        path.write_text(text, encoding="utf-8", newline="")
        print(f"FEMESH_HELP_POLISH={path.name} PASS")


if __name__ == "__main__":
    if sys.argv[1:] == ["--polish"]:
        polish()
    elif not sys.argv[1:]:
        main()
    else:
        raise SystemExit("Usage: python tools/UpdateFEMeshHelpV259.py [--polish]")
