"""Aggregate the six persistent-host runs and the recovered SAP timeout."""
import csv
import hashlib
from pathlib import Path

temp = Path.home() / "AppData/Local/Temp/opencode"
models = Path(__file__).resolve().parents[2] / "examples/atomic/models"
expected = {f"nTEST{i:03d}" for i in range(1, 37)}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


sap = {}
for name, selected in (("meshing-v302-sap-g1", set(range(1, 6))),
                       ("meshing-v302-sap-g1-retry", set(range(6, 13))),
                       ("meshing-v302-sap-g2", set(range(13, 25))),
                       ("meshing-v302-sap-g3", set(range(25, 37)))):
    group = temp / name
    with (group / "summary.csv").open(newline="", encoding="utf-8-sig") as stream:
        rows = list(csv.DictReader(stream))
    if name == "meshing-v302-sap-g1":
        assert len(rows) == 6 and rows[-1]["id"] == "nTEST006" and rows[-1]["status"] == "FAIL"
        rows = rows[:-1]
    assert {row["id"] for row in rows} == {f"nTEST{i:03d}" for i in selected}, name
    for row in rows:
        case = group / row["id"]
        assert row["status"] == "PASS" and row["model"] == "True" and row["backendJson"] != "0", row
        assert (case / f'{row["id"]}-meshed.sdb').is_file(), case
        assert row["id"] not in sap, row["id"]
        sap[row["id"]] = row
assert set(sap) == expected

bridge = {}
for index in range(1, 4):
    group = temp / f"meshing-v302-bridge-g{index}"
    for number in range(1 + 12 * (index - 1), 13 + 12 * (index - 1)):
        key = f"nTEST{number:03d}"
        case = group / key
        original = models / (key + ".sdb")
        assert original.is_file() and digest(case / (key + "-input.sdb")) == digest(original), key
        assert (case / "input.sha256").read_text(encoding="ascii").strip().lower() == digest(original), key
        assert (case / (key + ".bdb")).stat().st_size > 100, key
        assert "after_mesh_areas=" in (case / "completed.txt").read_text(encoding="utf-8"), key
        bridge[key] = True
assert set(bridge) == expected
print("MESHING_3_0_2_MATRIX=PASS SAP=36/36 BRIDGE=36/36 UNDO=not-run")
