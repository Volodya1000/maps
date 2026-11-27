# Belarus routing quality research

Approved approach: extend the existing Python/Verify research infrastructure from main
`89540b38efb127d58631659e1694b38080a38a4c` (merged PR #64). The user delegated remaining
design decisions and requested implementation, baseline, independent review, PR and CI;
do not merge the PR.

## Contract

Keep `samples/routing-crossing-mass-v1.json` byte-for-byte unchanged (88 cases),
including all points, options and assessment methodology. Existing six controls and
recovery/witness checks remain separate. Add 212 actual OSM scenarios, with 170
development cases and 42 sealed holdout cases. Freeze and hash the corpus before
any routing execution. Holdout results are evaluation evidence only: no individual
holdout geometry or failure may be used to design subsequent fixes.

Sample independently of router outcomes using pinned PBF/SQLite inputs, source IDs,
fixed seed, stable ordering, regional/category/distance/complexity quotas, unique
targets and spatial separation. Cover all six oblasts and Minsk city, countryside,
forest, field, road classes, roundabouts, interchanges, railway/stations,
underpasses, bridges/water, dense buildings/narrow passages, marsh/restricted areas,
and combinations. Record candidate availability, rejection reasons and any quota
shortfall; never silently fabricate coverage. OSM tags and geometries substantiate
category labels. Closed areas are a model limitation, not a new prohibition;
railway crossings are intentionally allowed and never counted as a defect.

## Approach and boundaries

Reuse `routing_regression.run_case/run_cases`, package/binary provenance, existing
geometry/topology auditors, recovery catalogue and portal witnesses. Separate
corpus generation, evidence classification and aggregation by responsibility.
A new .NET research runner would duplicate isolation/audit infrastructure and is
rejected. Add only measurement at Verify's composition root if necessary to obtain
per-process peak working set; never change routing algorithm or Domain policy.

Seven outcomes: valid route, invalid route, independently confirmed missed route,
justified refusal, indeterminate, resource limit, technical error. NoRoute and a
blocked direct segment never prove impossibility. Positive witnesses must pass
the existing independent full-polyline audit, endpoint/length/option checks and
actual search bounds. Justified refusal needs a proof such as independently
blocked endpoint or straight-line lower bound exceeding the configured maximum.
Unknown audits remain indeterminate. Resource/timeout evidence stays distinct
from malformed output or crashed processes. Group multiple evidence-backed facts
per case; confirmed facts about a blocked straight line are not confirmed causes
of a failed search. Mark hypotheses explicitly and distinguish algorithm,
model, geodata, resource and technical ownership.

## Reporting and execution

Compact Markdown plus versioned JSON: complete seven-way counts, regional,
category, split, complexity and distance strata; root-cause/hypothesis groups,
up to three development representatives, frequency/severity/estimated effort,
cold-process wall time/P95, measured peak RSS, route length/cost and diagnostic
fallback attempts/outcomes. Holdout is aggregated separately with no diagnostic
representatives in the improvement backlog. Keep raw results/logs/diagnostics in
artifacts. Comparative reports require identical corpus, package, options and
execution budgets; report changed statuses, audited-valid losses and length/cost
deltas, do not equate lower cost with correctness.

CI: retain frozen regression workflow; add separately dispatched full experiment,
bounded workers (default 2), per-process timeout (120 s), managed heap (1536 MiB),
job timeout and cancellation, verified country package and always-upload artifacts.
Local and CI use the same entry points. Baseline uses the current algorithm and
records algorithm source hashes separately from dirty harness changes. Freeze
corpus before baseline; failures discovered here inform future PRs only.

The experiment integrity gate requires complete unchanged evidence, no technical
execution failures, and all original regression/recovery/control gates. A separate
quality gate reports invalid/missed new routes without preventing a first baseline
from recording discoveries. A comparison fails on losses of audited-valid routes.

## Verification

Behavior tests cover deterministic selection and frozen integrity, geographic
and spatial splits, honest category evidence, classification precedence/proofs,
multiple causes and confidence, incomplete/mismatched evidence, comparison
compatibility, bounded output and complete inventories. Run existing data suite,
lint and desktop hooks; record unavailable platform checks honestly. Independent
whole-branch review follows implementation, and findings are fixed before PR/CI.
