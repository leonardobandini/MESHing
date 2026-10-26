"""Verify the 36 disposable CSiBridge BDB outputs and write a local CSV summary."""

import csv
import hashlib
import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main(folder: Path, previous_summary: Path | None = None) -> None:
    folder = folder.resolve()
    if not folder.is_dir() or "opencode" not in folder.parts:
        raise ValueError("Bridge results must be in an existing opencode test folder")
    manifest = json.loads((ROOT / "examples" / "atomic" / "manifest.json").read_text(encoding="utf-8"))
    expected = [f"nTEST{i:03d}" for i in range(1, 37)]
    if [item["id"] for item in manifest] != expected:
        raise ValueError("Unexpected atomic manifest")
    baseline = {}
    if previous_summary is not None:
        previous_summary = previous_summary.resolve()
        if "opencode" not in previous_summary.parts:
            raise ValueError("Baseline must be an earlier local regression summary")
        with previous_summary.open(encoding="utf-8", newline="") as stream:
            baseline = {item["id"]: item for item in csv.DictReader(stream)}
        if set(baseline) != set(expected) or any(item["status"] != "PASS" for item in baseline.values()):
            raise ValueError("The earlier Bridge summary does not contain 36 PASS cases")
    output = folder / "summary.csv"
    if output.exists():
        raise FileExistsError(output)
    rows = []
    for index, item in enumerate(manifest):
        name = item["id"]
        direct = folder / name
        grouped = folder / ("group-1" if index < 18 else "group-2") / name
        if direct.exists() and grouped.exists():
            raise ValueError(f"Ambiguous Bridge case folders: {name}")
        case = direct if direct.is_dir() else grouped
        original = ROOT / "examples" / "atomic" / "models" / (name + ".sdb")
        copied = case / (name + "-input.sdb")
        bdb = case / (name + ".bdb")
        trace = case / "completed.txt"
        if not all(path.is_file() for path in (original, bdb, trace)):
            raise ValueError(f"Missing Bridge test artifact: {name}")
        source_hash = sha(original)
        marker = case / "input.sha256"
        verified_copy = copied.is_file() and source_hash == sha(copied)
        verified_marker = marker.is_file() and marker.read_text(encoding="ascii").strip().lower() == source_hash
        verified_baseline = name in baseline and baseline[name]["source_sha256"] == source_hash
        if (not (verified_copy or verified_marker or verified_baseline) or
                (baseline and not verified_baseline) or bdb.stat().st_size < 100):
            raise ValueError(f"Source changed or BDB is empty: {name}")
        contents = trace.read_text(encoding="utf-8")
        elements = re.search(r"(?m)^elements=(\d+)\s*$", contents)
        areas = re.search(r"(?m)^after_mesh_areas=(\d+)(?:\s|$)", contents)
        if not elements or not areas or int(elements.group(1)) < 1 or int(areas.group(1)) < 1:
            raise ValueError(f"Incomplete mesh completion: {name}")
        rows.append({"id": name, "status": "PASS", "elements": elements.group(1),
                     "meshed_areas": areas.group(1), "source_sha256": source_hash,
                     "bdb_sha256": sha(bdb)})
    with output.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    print(f"FEMESH_BRIDGE_MATRIX=PASS COUNT={len(rows)} SUMMARY={output}")


if __name__ == "__main__":
    if len(sys.argv) not in (2, 3):
        raise SystemExit("Usage: python tools/VerifyFEMeshBridgeResults.py RESULT_ROOT [EARLIER_SUMMARY]")
    main(Path(sys.argv[1]), Path(sys.argv[2]) if len(sys.argv) == 3 else None)
