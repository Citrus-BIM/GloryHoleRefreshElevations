# Level rebinding implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development to implement the independent tasks below. User approved the compact UI and authorized implementation on 2026-10-05.

**Goal:** Repair copied opening tasks' native level association while preserving geometry, with automatic selection or a manual level for selected elements only.

**Architecture:** A pure level resolver supplies a Revit transaction service. The existing command refresh logic is shared between its ordinary path and the optional rebinding path. The WPF dialog provides one-shot inputs; these inputs never enter the automatic updater's XML settings.

**Tech stack:** C#, WPF, Revit 2019–2027 APIs, xUnit for pure rules. Work in the existing repository on `codex/gloryhole-level-rebind`; preserve the pre-existing modified data/GloryHoleRefreshElevationsCommand.txt and untracked README.md.

## Algorithm audit

- Standard task codes: wall rectangular 111/113/115; wall round 112/114/116; slab rectangular 121/123; slab round 122/124. Empty code permits only the four exact Пересечение family names. Unknown or conflicting profiles and finished openings are not rebound.
- Use actual LocationPoint.Z, not the shared report offset. Wall reference: insertion point (rectangular bottom / circular centre). Slab reference: insertion point minus 50/304.8 ft, matching the creators and existing refresh formula.
- Resolve against Level.ProjectElevation. Walls: greatest elevation at/below reference within 1e-6 ft. Slabs: minimum absolute distance. Equal-height candidates or equally near slab levels are ambiguous; no lower wall level is a skip. Explicit selection resolves one current-document UniqueId and is accepted only in selected scope.
- Require unhosted, upright, standalone, unpinned GenericModel OneLevelBased tasks, a writable native FAMILY_LEVEL_PARAM. A missing old level is recoverable: ignore its old offset. For a valid old level, confirm ProjectElevation + placement offset agrees with actual insertion Z. Prefer writable INSTANCE_ELEVATION_PARAM for placement (including slabs); allow the compatible free-host offset as a fallback. Reacquire and validate it after assigning an absent level.
- Placement: newOffset = saved insertionPoint.Z - target.ProjectElevation, including instances created without a level. For a valid old placement this is equivalent to compensating the old offset. Regenerate and verify point, orientation, type, sizes, bounding box and GH_GUID. Preserve the same instance, and thus its lineage and import metadata.
- Preserve report convention: base height = Level.Elevation; slab report offset = native offset - 50 mm.
- Suppress the registered GloryHole updater across all commits; restore its previous registry enabled state afterwards. XML UpdaterOn is independent.
- Use a TransactionGroup for one Undo and isolated per-element transactions/groups. Verify rebinding after commit before optional rounding; then refresh/round explicitly. Abort the entire group on regeneration failure; ordinary element failures roll back only that element and appear in the report.

## Tasks

- [x] Add RebindLevelRules.cs and focused tests for below/equal/above, negative elevations, 50 mm slab anchor, duplicate/tied levels, empty lists, profile classification and offset compensation. Run tests red, then green.
- [x] Add compact WPF section matching the approved mockup. Inputs: RebindToLevels, RebindToSelectedLevel, SelectedRebindLevelUniqueId. Manual radio is disabled outside selected scope and resets to automatic when scope changes to whole project. On check show the short warning. Rebind settings default off on every opening and are not persisted. Fix Space key handling; validate level and positive enabled rounding increments.
- [x] Add LevelRebindingService.cs, UpdaterSuspension.cs and result reporting. Keep native Revit transaction ownership here. Ordinary refresh isolates missing-level/parameter failures and reports skipped IDs; known finished openings keep refresh-only behavior when rebinding is enabled. Use the audited updater identity from the existing DDBImport implementation.
- [x] Extract current per-instance refresh methods without changing their formulas; pass real levels to the dialog and invoke rebinding only when checked. Enforce scope restrictions again in the service. Show changed/unchanged/skipped counts and reasons, and allow selection of skipped elements.
- [x] Run pure tests (96/96) and WPF smoke checks (38/38); build target configurations available locally (at least 2019, 2024, 2026). Check 2027 separately because its installed API package may require a newer .NET SDK than the repository currently declares. Do not overwrite distributed DLLs with an arbitrary target build.
- [x] Independent code review, fix actionable findings, record real-model acceptance checks and actual validation limits.

## Real-model acceptance

1. Copy rectangular and round wall/slab tasks to another storey; disable rounding and confirm level changes with identical world geometry and GH_GUID.
2. Repeat with shifted project/survey base settings and a slab close to a level-selection midpoint; verify the 50 mm correction.
3. Verify explicit target only for selected elements, including a level above the task (negative native offset).
4. Verify duplicate/tied levels, tasks below all levels, pinned/grouped/nested/hosted/tilted tasks and read-only parameters produce skips without partial modifications.
5. With updater enabled/disabled, verify previous registry state is restored. With explicit rounding enabled, verify only its requested movement occurs after successful rebinding.
6. Verify one Undo reverses the whole operation and failures never report provisional changes as completed.

Implementation notes and reproducible checks: see ../../level-rebinding.md. User requested merge and push to master after verification.
