# Read-only opening level replacement

**Goal:** Rebind selected hosted openings to an explicitly selected level, recreating an instance when its native level is read-only.

**Approved scope:** Keep `GH_GUID`, type, host, world placement, physical dimension parameters, user data and CITRUS task relationships. New Revit IDs are accepted. Tags and drawing dimensions need not survive and do not block the operation. Tasks retain existing automatic and project-wide modes. Finished openings cannot use those modes for rebinding.

**Architecture:** Retain the verified in-place path for writable levels. Capture replacement data before deletion; replace within the existing per-item rollback group; verify native level, dimensions, placement and cutting relationships after regeneration and commit. Commit-time failure restores the original. Stage the optional model journal with the replacement; defer file persistence and networking until the entire outer group commits, off the Revit thread.

**Tech stack:** C#, Revit API 2019–2027, WPF, xUnit with the existing Revit test doubles.

## Work

- [x] Add behavior tests for selected/manual-only opening scope, read-only replacement, unchanged target, metadata preservation and rollback.
- [x] Implement replacement snapshots and creation with the same symbol/host and explicit target level. Preserve pins, parameter values, physical dimensions, phase and workset; reject unintended deletion of other model elements.
- [x] Preserve CITRUS source marker and lineage, update its opening signature, and map optional Hub result identity after successful completion.
- [x] Explain the selected/manual restriction in the existing window, suppress opening rounding during rebinding, report recreated counts and preserve task modes.
- [x] Review the complete diff; run tests and build all R2019–R2027 outputs without PDBs, preserving settings.
- [x] Update runtime smoke instructions and prepare both repositories for master publication with direct bin links.

Verification: Refresh 262/262; Cutter 319/319; Cutter metadata boundary 5/5. All 18 production builds succeeded. Native Revit smoke tests below remain pending.

## Native Revit acceptance (requires the user's model)

1. Select a hosted opening with read-only Level; choose the required level. Verify its actual Level and world coordinates, the same physical dimension parameters and `GH_GUID`, and its host cut.
2. Repeat with a pinned opening and with wall/floor, rectangular/round supported families. Verify the final pin and working set.
3. Repeat with a batch containing an invalid replacement. Confirm the failed item remains intact and the report identifies it; successful items remain successful.
4. Confirm automatic/project-wide task rebinding does not recreate or rebind ready openings.
5. Without Hub configuration, confirm no Hub setup prompt, network wait or journal write is introduced. With Hub, verify old/new result identity and subsequent Cutter recognition.

Builds and stub tests cannot establish actual RFA cutting behavior. Report that limitation explicitly.
