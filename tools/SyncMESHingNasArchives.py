"""Back up public corresponding sources and disposable local candidates to Satoshi.

Never replaces a different NAS file. Public releases are checked against GitHub
asset digests; candidates are checked against their local SHA-256 files.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import uuid
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parent.parent
NAS = Path(r"\\satoshi.b2\csi\MESHing")
HISTORY = NAS
CANDIDATES = NAS / "candidate"
ARTIFACTS = ROOT / "artifacts"
TEMP = Path(os.environ["TEMP"]) / "opencode" / "meshing-nas-published-sources-20261103"
UPSTREAM = ("gmsh-4.15.2-source.tgz", "mmg-5.5.2-source.tar.gz")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def gh(*args):
    return subprocess.check_output(("gh", *args), text=True, encoding="utf-8")


def put_checked(source, destination, expected):
    if not source.is_file() or digest(source) != expected:
        raise RuntimeError(f"Invalid source file: {source}")
    if destination.exists():
        if not destination.is_file() or digest(destination) != expected:
            raise RuntimeError(f"NAS file differs; refusing to overwrite: {destination}")
        return False
    if not destination.parent.is_dir():
        raise FileNotFoundError(destination.parent)
    temporary = destination.with_name(destination.name + ".copy-" + uuid.uuid4().hex)
    try:
        shutil.copyfile(source, temporary)
        if digest(temporary) != expected:
            raise IOError(f"NAS copy verification failed: {temporary}")
        os.rename(temporary, destination)
        if digest(destination) != expected:
            raise IOError(f"NAS final verification failed: {destination}")
    finally:
        if temporary.exists():
            temporary.unlink()
    return True


def release_assets(repo, version):
    info = json.loads(gh("release", "view", "v" + version, "--repo", repo,
                         "--json", "isDraft,assets"))
    if info["isDraft"]:
        raise RuntimeError(f"Draft must not enter published backup: {repo} v{version}")
    return {asset["name"]: asset for asset in info["assets"]}


def published_releases():
    releases = json.loads(gh("api", "repos/leonardobandini/MESHing/releases?per_page=100"))
    versions = [release["tag_name"][1:] for release in releases
                if not release["draft"] and re.fullmatch(r"v3\.\d+\.\d+", release["tag_name"])]
    return tuple(("leonardobandini/MESHing", version, "MESHing")
                 for version in sorted(versions, key=lambda value: tuple(map(int, value.split(".")))))


def github_file(repo, version, name, asset):
    sha = asset["digest"].removeprefix("sha256:").lower()
    if len(sha) != 64:
        raise ValueError(f"Missing GitHub digest: {repo} {name}")
    local = ARTIFACTS / name
    if local.is_file() and digest(local) == sha:
        return local, sha
    archived = HISTORY / version / name
    if archived.is_file() and digest(archived) == sha:
        return archived, sha
    folder = TEMP / (repo.split("/")[-1] + "-" + version)
    folder.mkdir(parents=True, exist_ok=True)
    file = folder / name
    if not file.exists() or digest(file) != sha:
        if file.exists():
            raise RuntimeError(f"Unexpected existing download differs: {file}")
        gh("release", "download", "v" + version, "--repo", repo,
           "--pattern", name, "--dir", str(folder))
    if digest(file) != sha:
        raise RuntimeError(f"Downloaded source differs from GitHub digest: {file}")
    return file, sha


def public_sources():
    for base in (NAS, HISTORY, ARTIFACTS, TEMP.parent):
        if not base.is_dir():
            raise FileNotFoundError(base)
    TEMP.mkdir(exist_ok=True)
    for repo, version, prefix in published_releases():
        assets = release_assets(repo, version)
        source = f"{prefix}-{version}-source.zip"
        names = (source, source + ".sha256") + tuple(
            item for upstream in UPSTREAM for item in (upstream, upstream + ".sha256"))
        if any(name not in assets for name in names):
            raise RuntimeError(f"Missing published corresponding source asset: {repo} v{version}")
        files = {name: github_file(repo, version, name, assets[name]) for name in names}
        checksum = files[source + ".sha256"][0].read_text(encoding="ascii").strip()
        if checksum.lower() != f"{files[source][1]}  {source}".lower():
            raise RuntimeError(f"Published corresponding-source checksum differs: {repo} v{version}")
        for upstream in UPSTREAM:
            checksum = files[upstream + ".sha256"][0].read_text(encoding="ascii").strip()
            if checksum.lower() != f"{files[upstream][1]}  {upstream}".lower():
                raise RuntimeError(f"Published upstream checksum differs: {repo} v{version} {upstream}")
        folder = HISTORY / version
        folder.mkdir(exist_ok=True)
        added = sum(put_checked(path, folder / name, sha) for name, (path, sha) in files.items())
        print(f"PUBLIC=PASS repo={repo} version={version} files={len(files)} new={added} path={folder}", flush=True)


def candidate(version=None):
    if not NAS.is_dir():
        raise FileNotFoundError(NAS)
    if version is None:
        version = ET.parse(ROOT / "SapQuadTransitionPlugin" / "SapGmshReplacement.csproj").findtext(
            "./PropertyGroup/Version")
    if not re.fullmatch(r"3\.\d+\.\d+", version):
        raise ValueError("Expected a versioned MESHing 3.x candidate")
    source = ARTIFACTS / ("candidate-v" + version)
    if not source.is_dir():
        raise FileNotFoundError(source)
    archives = tuple(sorted(file for file in source.iterdir() if file.is_file() and
                            not file.name.endswith(".sha256")))
    if len(archives) != 5 or len(list(source.iterdir())) != 10:
        raise RuntimeError("Expected exactly ten verified local candidate assets")
    checked = {}
    for archive in archives:
        checksum = source / (archive.name + ".sha256")
        sha = digest(archive)
        if not checksum.is_file() or checksum.read_text(encoding="ascii").strip().lower() != (
                f"{sha}  {archive.name}".lower()):
            raise RuntimeError(f"Invalid local candidate checksum: {archive.name}")
        checked[archive.name] = (archive, sha)
        checked[checksum.name] = (checksum, digest(checksum))
    if checked[f"MESHing-{version}.zip"][1] != checked["MESHing-latest.zip"][1]:
        raise RuntimeError("Candidate Latest alias differs from versioned installer")
    CANDIDATES.mkdir(exist_ok=True)
    folder = CANDIDATES / version
    folder.mkdir(exist_ok=True)
    added = sum(put_checked(path, folder / name, sha) for name, (path, sha) in checked.items())
    print(f"CANDIDATE=PASS files={len(checked)} new={added} path={folder}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("which", choices=("published", "candidate", "all"))
    parser.add_argument("--version", help="Candidate version (defaults to the current project version)")
    args = parser.parse_args()
    if args.which in ("published", "all"):
        public_sources()
    if args.which in ("candidate", "all"):
        candidate(args.version)
