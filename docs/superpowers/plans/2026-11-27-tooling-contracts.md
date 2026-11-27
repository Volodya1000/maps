# Tooling contracts implementation plan

> **For agentic workers:** Use superpowers:executing-plans; independent tooling tasks may use superpowers:dispatching-parallel-agents.

**Goal:** Fulfil issue #60 with reproducible Python/C# boundaries and remove unused tooling.
**Architecture:** C# Domain remains authoritative; Python preparation and independent geometry audits stay external. Use the real SQLite reader and bundled-data installer in integration tests.
**Tech Stack:** Python 3.12, uv locked, unittest, .NET 8/xUnit; retain conditional net6.0 Win7.
**Spec:** https://github.com/Volodya1000/maps/issues/60

## Global constraints
Preserve Win7 x86 build/workflow/locks, SQLite v1/v2, historical baselines, current CI and offline runtime. No routing policy/UI changes or disabled diagnostics.

## Review focus
Ambiguous/stale coverage; missing underpass evidence; exact kind renames; legacy schemas; chunk ordering/corruption. Each belongs to the task below that owns its reader/audit.

## Tasks
- [x] Remove unused Win7 net48/Mono/guest code, old APK installer/tests, and their active consumers. Preserve historical evidence. Validate projects and search references.
- [x] Add producer-to-real-C# reader contract tests for SQLite v3/kinds/underpass relations and Android chunks. Exercise v1/v2 and malformed inputs. Run the narrow contract tests then data/desktop gates.
- [x] Make routing experiment options and historical/current modes explicit; audit exact underpass links and evidence independently. Add positive/negative contact tests, run geodata suite.
- [x] Reject ambiguous coverage reports, test stale data; test network traces and eliminate duplicate policy assertions. Run tooling tests.
- [x] Add typed cross-language JSON models and mandatory incremental static checking to the same local/CI lint entry point. Update locks and run the checker.
- [ ] Update current tooling/Win7 documentation and changelog; run lint, data, desktop, Win7 package and applicable hooks, review the whole diff, commit and open PR closing #60.
