# Translate RESW Skill

This skill is designed for manual translation and localization of FluentGallery's `.resw` resource files.

## Recommendation

For best results, it is **highly recommended to use a model with a large context window** (e.g., Gemini 1.5 Pro, Claude 3.5 Sonnet). 

Localization tasks often require:
1. Reading the entire source `en-US/Resources.resw` to maintain consistency.
2. Comparing with existing translations in the target language.
3. Ensuring the XML schema and root tags remain intact across large files.

Models with smaller context windows may truncate the file or lose track of XML tag nesting, leading to build errors like `PRI224` or `PRI277`.

## Validation Helpers

- `validate_all.py`: checks XML syntax, root structure, duplicate keys, and missing keys using the `en-US + zh-CN` union.

## Features
- Manual translation of resource strings.
- Unified validation script `validate_all.py` to check XML syntax, duplicate keys, and incomplete locale coverage.
- Maintenance of consistent key-value pairs across multiple languages.

## Usage
Refer to [SKILL.md](SKILL.md) for detailed operational instructions.
