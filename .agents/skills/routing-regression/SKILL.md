---
name: routing-regression
description: Verify a proposed or completed cable-route algorithm change involving terrain cost, obstacles, bridges, A*, route geometry, cancellation, stale results, determinism, or performance. Do not use for unrelated UI or persistence changes.
---

# Routing regression

Use this workflow only when the diff can affect route planning or its observed
results. Treat [docs/routing-algorithm.md](../../../docs/routing-algorithm.md),
Domain traversal rules, and existing fixtures/tests as the source of truth; do
not invent terrain or bridge policy.

1. Inspect the diff and map affected behavior to the named tests in
   `tests/CableRoutePlanner.Tests/Infrastructure/RoutingTests.cs`, then include
   `DomainTests`, `PlanningSessionTests`, or `PresentationTests`
   when their boundary changed. Check the algorithm document for the applicable
   invariant: distinct forest, field, clearing, and road costs; blocked
   marsh/lake/river/building; explicit approval
   or `BridgeCandidate`, full-segment geometry, waypoint order, cancellation,
   revision/late-result handling, determinism, or resource limit. Reuse
   `RepeatedPlanningPreservesObstacleAvoidanceAndMetrics` for deterministic
   output and `CancelledOldBuildCannotPublishAfterANewerBuildStarts` or
   `ReorderingDuringCalculationCancelsAndBuildsLatestOrder` for changed-input
   publication; do not replace them with timing-only evidence.
2. Add or select behavior-level cases using the existing synthetic fixtures.
   A route test must assert a public route/error/metric, not A* queue steps or
   private calls. Cover both sides of a modified decision: permitted and
   rejected traversal; a route and no-route/actionable error where relevant.
   Preserve the distinction between an OSM bridge candidate and an approved
   engineering crossing.
3. Run the narrow affected tests first:

   ```sh
   dotnet restore CableRoutePlanner.Desktop.slnf --locked-mode
   dotnet test CableRoutePlanner.Desktop.slnf --no-restore --filter FullyQualifiedName~RoutingTests
   ```

   Run the equivalent filtered class for any additional boundary in step 1.
   For changed routing production code, finish with the repository gate:

   ```sh
   uv run --locked python tools/check.py desktop
   ```

4. Report `scenario | named test | command | result`. Record the input/terrain
   facts and observable expected outcome, cancellation or stale-result outcome
   when applicable, and measured diagnostics only as comparative evidence.
   Mark timing or memory as `NOT VERIFIED` when a comparable Release run is not
   available; do not turn the documented example timings into a universal limit.

Done when every affected documented rule has an identified passing test or a
specific gap, the relevant command results are recorded, and no unsupported
business rule was assumed.
