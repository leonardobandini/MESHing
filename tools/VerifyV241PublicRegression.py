"""Verify all 36 SAP cases across the completed package-specific regression runs."""
import csv
from pathlib import Path


ROOT = Path.home() / 'AppData' / 'Local' / 'Temp' / 'opencode'
SOURCES = (
    ROOT / 'sqm-v241-public-final-regression' / 'group-1' / 'summary.csv',
    ROOT / 'sqm-v241-public-final-regression' / 'group-2' / 'summary.csv',
    ROOT / 'sqm-v241-public-recheck-025-027' / 'summary.csv',
    ROOT / 'sqm-v241-public-recheck-028-036' / 'summary.csv',
)


def main():
    rows = []
    for source in SOURCES:
        with source.open(newline='', encoding='utf-8-sig') as stream:
            group = list(csv.DictReader(stream))
        if not group or any(entry['status'] != 'PASS' or entry['backendJson'] != '1' or
                            entry['screenshot'] != 'True' or entry['model'] != 'True'
                            for entry in group):
            raise ValueError('Missing or failed SAP case: ' + str(source))
        rows.extend(group)
        print(f'VERIFIED_GROUP={source.parent.name} PASS={len(group)}')
    expected = [f'nTEST{number:03d}' for number in range(1, 37)]
    if [row['id'] for row in rows] != expected:
        raise ValueError('The public candidate has missing, duplicate or unordered atomic cases')
    print('V2_2_0_41_FINAL_SAP_REGRESSION=PASS COUNT=36')


if __name__ == '__main__':
    main()
