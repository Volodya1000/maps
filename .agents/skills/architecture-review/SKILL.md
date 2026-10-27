---
name: architecture-review
description: Review a proposed change to Cable Route Planner for layer-boundary, MVVM, dependency-injection, routing ownership, SOLID, or unnecessary-abstraction violations. Use when design or cross-project dependencies are in scope, not for a local formatting-only change.
---

# Architecture review

Read `docs/architecture.md` and `AGENTS.md`, then inspect only the relevant
diff and project references. Apply the documented graph: Domain is BCL-only;
Application owns contracts/session/revision; Infrastructure owns NTS, spatial
indices and A*; Persistence owns SQLite; ViewModels are UI-technology-free;
UI owns AXAML/maps; Desktop, Android, and Verify are composition roots.

For each changed dependency or abstraction, record `source -> target | allowed
or violation | evidence`. Check that routing policy remains in Domain, geometry
remains in Infrastructure, UI does not gain domain rules, registrations stay in
composition roots, cancellation/late results preserve the documented ownership,
and an interface or wrapper solves a real substitutability/composition need.
Do not demand new abstractions merely to satisfy a pattern.

Run the existing architecture and compilation gate when C# boundaries changed:

```sh
uv run --locked python tools/check.py lint
uv run --locked python tools/check.py desktop
```

End with `PASS`, `VIOLATION`, or `NOT VERIFIED` per finding and name the exact
file/edge. Done when every new cross-layer edge is accounted for and no result
is inferred from convention alone.
