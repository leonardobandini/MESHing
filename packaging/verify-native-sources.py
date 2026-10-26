"""Verify pinned native Gmsh dependency sources and stage their original licenses.

This checks file provenance, not the unpublished vendor build recipe or legal
compliance of the official binary. Do not report it as certification of either.
"""

from pathlib import Path
import hashlib
import json
import shutil
import sys
import tarfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / 'packaging' / 'native-gmsh-sources.json'
EXPECTED = {
    'occt-7.8.1-source.tar.gz', 'fltk-1.3.11-source.tar.gz',
    'med-4.1.0-source.tar.gz', 'mmg-5.5.2-source.tar.gz',
}


def sha(path, algorithm='sha256'):
    digest = hashlib.new(algorithm)
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def main(stage):
    stage = stage.resolve()
    version = ET.parse(ROOT / 'SapQuadTransitionPlugin' / 'SapGmshReplacement.csproj').findtext('./PropertyGroup/Version')
    if not version or stage != (ROOT / 'artifacts' / ('Sap2000QuadMesher-' + version)).resolve():
        raise ValueError('Only the generated MESHing ' + str(version) + ' staging directory may be updated')
    licenses = stage / 'licenses'
    if not licenses.is_dir():
        raise FileNotFoundError(licenses)
    manifest = json.loads(MANIFEST.read_text(encoding='utf-8'))
    if manifest['schema'] != 1 or {item['archive'] for item in manifest['components']} != EXPECTED:
        raise ValueError('Unexpected source manifest schema or dependency list')
    dll = stage / 'mesher' / '_internal' / 'gmsh-4.15.dll'
    if sha(dll) != manifest['gmshDllSha256']:
        raise ValueError('Gmsh DLL differs from the audited official Windows wheel')
    prepared = {}
    for item in manifest['components']:
        name = item['archive']
        if Path(name).name != name or not name.endswith('.tar.gz'):
            raise ValueError('Unsafe source filename')
        cached = ROOT / 'packaging' / 'cache' / name
        if sha(cached) != item['sha256']:
            raise ValueError('Source SHA-256 mismatch: ' + name)
        if item.get('sha512') and sha(cached, 'sha512') != item['sha512']:
            raise ValueError('Source SHA-512 mismatch: ' + name)
        if not item['url'].startswith('https://'):
            raise ValueError('Upstream source must use HTTPS: ' + name)
        with tarfile.open(cached, 'r:gz') as archive:
            names = set(archive.getnames())
            prefix = archive.getnames()[0].split('/')[0] + '/'
            if name == 'mmg-5.5.2-source.tar.gz':
                cmake = archive.extractfile(prefix + 'CMakeLists.txt').read().decode('utf-8', 'replace')
                for field, expected in [('MAJOR', '5'), ('MINOR', '5'), ('PATCH', '2')]:
                    if ('CMAKE_RELEASE_VERSION_' + field + ' "' + expected + '"') not in cmake:
                        raise ValueError('Official MMG source version does not match its filename')
            for original, renamed in item['licenseFiles'].items():
                if Path(renamed).name != renamed or renamed in prepared:
                    raise ValueError('Unsafe or duplicate staged license')
                member = prefix + original
                if member not in names:
                    raise ValueError('Upstream license missing from ' + name + ': ' + original)
                contents = archive.extractfile(member).read()
                if len(contents) < 100 or len(contents) > 100_000:
                    raise ValueError('Suspicious upstream license size: ' + member)
                prepared[renamed] = contents
        if name == 'mmg-5.5.2-source.tar.gz':
            artifact = ROOT / 'artifacts' / name
            if artifact.exists():
                if sha(artifact) != item['sha256']:
                    raise ValueError('Existing artifact is not the pinned source: ' + name)
            else:
                shutil.copyfile(cached, artifact)
            checksum = ROOT / 'artifacts' / (name + '.sha256')
            expected_checksum = item['sha256'] + '  ' + name + '\n'
            if checksum.exists():
                if checksum.read_text(encoding='ascii') != expected_checksum:
                    raise ValueError('Existing source checksum differs: ' + name)
            else:
                checksum.write_text(expected_checksum, encoding='ascii')
            location = 'RELEASE_ASSET'
        else:
            location = 'MESHING_SOURCE_ZIP'
        print('NATIVE_SOURCE=' + name + ' SHA256=' + item['sha256'] + ' LOCATION=' + location)
    notices = manifest.get('noticeOnly', [])
    if {item['staged'] for item in notices} != {
        'CGNS-LICENSE.txt', 'PETSC-LICENSE.txt',
        'HDF5-LICENSE.txt', 'HDF5-LBNL-LICENSE.txt',
    }:
        raise ValueError('Unreviewed list of license-only references')
    for notice in notices:
        cached = ROOT / 'packaging' / 'cache' / notice['cached']
        if (Path(notice['cached']).name != notice['cached'] or
                Path(notice['staged']).name != notice['staged']):
            raise ValueError('Unsafe notice filename')
        if not cached.exists():
            # Corresponding-source ZIP already carries the exact audited texts.
            cached = ROOT / 'licenses' / notice['staged']
        if sha(cached) != notice['sha256'] or not notice['url'].startswith('https://'):
            raise ValueError('Upstream notice mismatch: ' + notice['cached'])
        prepared[notice['staged']] = cached.read_bytes()
    for name, contents in prepared.items():
        (licenses / name).write_bytes(contents)
    print('NATIVE_LICENSES=STAGED COUNT=' + str(len(prepared)))


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('Usage: python packaging/verify-native-sources.py GENERATED_STAGE')
    main(Path(sys.argv[1]))
