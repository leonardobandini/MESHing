"""Verify all 36 SAP cases from the completed 2.0.42 package-specific runs."""
import csv
from pathlib import Path


ROOT = Path.home() / 'AppData' / 'Local' / 'Temp' / 'opencode'
GROUPS = (
    ('sqm-v242-release-regression-01-12', 1, 12),
    ('sqm-v242-release-regression-13-24', 13, 16),
    ('sqm-v242-regression-recheck-017', 17, 17),
    ('sqm-v242-regression-recheck-018', 18, 18),
    ('sqm-v242-regression-018-024', 19, 24),
    ('sqm-v242-release-regression-25-36', 25, 36),
)


def main():
    checked = []
    for folder, first, last in GROUPS:
        with (ROOT / folder / 'summary.csv').open(newline='', encoding='utf-8-sig') as stream:
            rows = list(csv.DictReader(stream))
        expected = [f'nTEST{index:03d}' for index in range(first, last + 1)]
        selected = [row for row in rows if row['id'] in expected]
        if ([row['id'] for row in selected] != expected or
                any(row['status'] != 'PASS' or row['backendJson'] != '1' or
                    row['screenshot'] != 'True' or row['model'] != 'True'
                    for row in selected)):
            raise ValueError('Missing, failed or unordered SAP case: ' + folder)
        checked.extend(selected)
        print(f'VERIFIED_GROUP={folder} PASS={len(selected)}')
    if [row['id'] for row in checked] != [f'nTEST{index:03d}' for index in range(1, 37)]:
        raise ValueError('Missing or duplicate 2.0.42 SAP case')
    print('V2_2_0_42_PUBLIC_SAP_REGRESSION=PASS COUNT=36')


if __name__ == '__main__':
    main()
