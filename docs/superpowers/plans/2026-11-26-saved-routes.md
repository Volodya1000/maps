# Saved routes implementation plan

Goal: keep complete local route calculations on Desktop and Android, restore them without planning, and reuse GPS traversal.

Architecture: Application owns the saved calculation contract and validated session replacement. Persistence stores versioned JSON beside user settings using atomic replacement. ViewModels expose save/list/open/delete commands; shared Avalonia screens use the existing router and map adapter.

Constraints: UTC storage/local display; independent IDs and duplicate names; all alternative metadata and original ordered waypoints; frozen calculation options; .NET 6 Windows 7 API compatibility; unchanged routing rules and GPS tracker.

- [x] Persistence: write temporary-store round-trip and independent deletion tests, observe failure, implement validation and atomic JSON storage, wire DI to the settings directory.
- [x] Restoration: test save/open through commands after restart, singleton/blank name, stale planner completion and read failure. Implement session replacement and map commands, invalidating pending work only after a complete read succeeds.
- [x] Navigation/UI: add Saved routes singleton screen and typed view factory; preserve unsaved settings guard. Keep navigation outside the mobile bottom sheet; expose confirmation and optional route name. Restore Route tab and fit geometry after layout.
- [x] GPS: test cold restoration, explicit start, consecutive trustworthy positions, pause/restart/explicit resume, and different geometry isolation using real files.
- [ ] Verification: run lint/desktop/Android and legacy build when prerequisites permit; review the branch; add changelog and create one PR from current main with separate automated/device/unverified evidence.

Review focus: corrupt snapshots cannot partially replace work; delayed point moves/reorders cannot clear restored routes; stale camera restoration cannot hide an opened route; duplicate records delete by ID; no automatic traversal on open.

Execution: inline. Detailed user requirements are the specification. Routine implementation choices are delegated by the request; no additional design approval is needed.

Review: fixed incomplete snapshot schema validation and mobile route fitting above the sheet, both with failing then passing behavior tests. Added late placement/reorder and same-ID/different-geometry GPS regressions. Official Avalonia Docs MCP is unavailable in this session; compiled markup and headless UI checks are used.
