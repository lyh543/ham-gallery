import os
import sys
from typing import List
import xml.etree.ElementTree as ET

# ---------------------------------------------------------------------------
# Project-specific constants.  These are the authoritative base locales whose
# union defines the complete key set.  Every base file is cross-validated
# against the others, and all target locales are checked for coverage.
# ---------------------------------------------------------------------------
BASE_FILES: List[str] = [
    'FluentGallery/Strings/en-US/Resources.resw',
    'FluentGallery/Strings/zh-CN/Resources.resw',
]

TARGET_FILES: List[str] = [
    'FluentGallery/Strings/ja-JP/Resources.resw',
    'FluentGallery/Strings/ko-KR/Resources.resw',
    'FluentGallery/Strings/de-DE/Resources.resw',
    'FluentGallery/Strings/fr-FR/Resources.resw',
    'FluentGallery/Strings/es-ES/Resources.resw',
]


def load_tree(file_path: str):
    if not os.path.exists(file_path):
        print(f'ERROR: File not found: {file_path}')
        return None

    try:
        return ET.parse(file_path)
    except ET.ParseError as e:
        print(f'ERROR: XML Parse Error in {file_path}: {e}')
        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                lines = f.readlines()
                line_no, _ = e.position
                if 1 <= line_no <= len(lines):
                    context = lines[line_no - 1].strip()
                    print(f'Context (Line {line_no}): {context}')
        except Exception:
            pass
        return None


def extract_keys(file_path: str) -> List[str]:
    tree = load_tree(file_path)
    if tree is None:
        return []
    return [
        data.get('name')
        for data in tree.getroot().findall('data')
        if data.get('name')
    ]


def validate_structure(file_path: str):
    tree = load_tree(file_path)
    if tree is None:
        return False, []

    root = tree.getroot()
    if root.tag != 'root':
        print(
            f'ERROR: Root tag must be <root>, found <{root.tag}> '
            f'in {file_path}'
        )
        return False, []

    keys = []
    duplicates = []
    seen = set()

    for index, data in enumerate(root.findall('data')):
        name = data.get('name')
        if not name:
            print(
                f"ERROR: <data> element at index {index} is missing "
                f"'name' attribute in {file_path}"
            )
            return False, []

        if data.find('value') is None:
            print(
                f"ERROR: Key '{name}' is missing <value> child element "
                f'in {file_path}'
            )
            return False, []

        if name in seen:
            duplicates.append(name)
        else:
            seen.add(name)
            keys.append(name)

    if duplicates:
        print(f'ERROR: Duplicate keys found in {file_path}:')
        for duplicate in duplicates:
            print(f'  - {duplicate}')
        return False, keys

    print(f'SUCCESS: {file_path} is valid.')
    return True, keys


def validate_base_cross(base_files: List[str]) -> bool:
    """Cross-validate base files: every key in one must exist in all."""
    all_keys: List[List[str]] = []
    for path in base_files:
        keys = extract_keys(path)
        if not keys:
            return False
        all_keys.append(keys)

    union_keys = list(dict.fromkeys(k for ks in all_keys for k in ks))
    success = True
    for path, keys in zip(base_files, all_keys):
        key_set = set(keys)
        missing = [k for k in union_keys if k not in key_set]
        if missing:
            success = False
            print(f'BASE MISSING: {path}')
            for k in missing:
                print(f'  - {k}')
            print(f'  -> total missing: {len(missing)}')
        else:
            print(f'BASE OK: {path} is in sync with all base files')
    return success


def validate_missing_keys(
    base_files: List[str], target_paths: List[str]
) -> bool:
    all_keys: List[List[str]] = []
    for path in base_files:
        keys = extract_keys(path)
        if not keys:
            return False
        all_keys.append(keys)

    base_keys = list(dict.fromkeys(k for ks in all_keys for k in ks))
    base_key_set = set(base_keys)
    labels = ' + '.join(
        os.path.basename(os.path.dirname(p)) for p in base_files
    )
    print(
        f'Base key set built from {labels}: '
        f'{len(base_keys)} unique keys'
    )
    for path, keys in zip(base_files, all_keys):
        label = os.path.basename(os.path.dirname(path))
        print(f'{label} keys: {len(keys)}')

    missing_any = False
    for path in target_paths:
        locale_keys = set(extract_keys(path))
        if not locale_keys:
            missing_any = True
            continue

        missing = [k for k in base_keys if k not in locale_keys]
        if missing:
            missing_any = True
            print(f'MISSING: {path}')
            for k in missing:
                print(f'  - {k}')
            print(f'  -> total missing: {len(missing)}')
        else:
            print(f'OK: {path} has all {len(base_key_set)} base keys')

    return not missing_any


def main():
    # When called without arguments, use the project-level constants.
    # Pass explicit paths to override: base files first, then targets.
    args = sys.argv[1:]
    if args:
        # Minimal CLI: first N=len(BASE_FILES) args are bases, rest targets.
        n = len(BASE_FILES)
        base_files = args[:n] if len(args) >= n else BASE_FILES
        target_files = args[n:] if len(args) > n else TARGET_FILES
    else:
        base_files = BASE_FILES
        target_files = TARGET_FILES

    success = True

    # 1. Structural validation for all files.
    for path in base_files + target_files:
        structure_ok, _ = validate_structure(path)
        if not structure_ok:
            success = False

    # 2. Cross-validate base files against each other.
    print()
    print('--- Base file cross-validation ---')
    if not validate_base_cross(base_files):
        success = False

    # 3. Check that every target locale covers the full base key set.
    print()
    print('--- Target locale coverage check ---')
    if not validate_missing_keys(base_files, target_files):
        success = False

    return 0 if success else 1


if __name__ == '__main__':
    raise SystemExit(main())
