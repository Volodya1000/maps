"""Deterministic, evidence-bounded conclusions for the Belarus routing study."""

import math
from collections import Counter, defaultdict

from routing_crossing_pilot import distance
from routing_regression import complete_no_route_error, search_meshes

OUTCOMES = (
    "valid_route",
    "invalid_route",
    "confirmed_missed_route",
    "justified_refusal",
    "indeterminate",
    "resource_limit",
    "technical_error",
)


def percentile(values, fraction=0.95):
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    low, high = math.floor(position), math.ceil(position)
    return round(ordered[low] + (ordered[high] - ordered[low]) * (position - low), 3)


def within_search(record, geometry):
    meshes = search_meshes(record.get("diagnostics"))
    if not meshes or not geometry:
        return False
    mesh = meshes[-1]
    return all(
        mesh["West"] <= point["Longitude"] <= mesh["East"]
        and mesh["South"] <= point["Latitude"] <= mesh["North"]
        for point in geometry
    )


def research_audit_status(audit, proofs):
    """Keep an auditor/model conflict uncertain; never turn it into permission."""
    if audit.get("status") != "violation" or proofs is None:
        return audit.get("status")
    findings = audit.get("findings", [])
    verified = {
        proof.get("findingIndex")
        for proof in proofs
        if proof.get("kind") == "independent_water_violation"
    }
    if not findings:
        return "violation"
    for index, finding in enumerate(findings):
        if (
            finding.get("allowed") is True
            or finding.get("reason") == "shared_road_section"
        ):
            continue
        if finding.get("reason") == "water_without_opening":
            if index in verified:
                return "violation"
            continue
        return "violation"
    return "unknown"


def classify(record, scenario, witnesses=()):
    """Consume runner/auditor facts; never infer global absence from finite search."""
    process = record.get("process", {})
    if process.get("timeout") is True:
        return {"outcome": "resource_limit", "proof": "process_time_budget"}
    if process.get("timeout") is not False or process.get("returncode") != 0:
        return {"outcome": "technical_error", "proof": "process_failure"}
    status = record.get("status")
    failure = record.get("failureReason")
    if failure in {
        "missing_result",
        "incomplete_diagnostics",
        "evidence_error",
        "process_failure",
        "process_timeout",
        "raw_identity_mismatch",
    }:
        return {"outcome": "technical_error", "proof": failure}
    if (
        status == "resource_limit"
        and (record.get("error") or {}).get("reason") == "ResourceLimit"
        and failure in (None, "inconsistent_result")
    ):
        return {"outcome": "resource_limit", "proof": "typed_resource_limit"}
    if status == "invalid_input":
        error = record.get("error") or {}
        proof = record.get("inputRefusalProof") or {}
        if (
            error.get("reason") in ("BlockedTerrain", "PointOutsidePackage")
            and proof.get("reason") == error["reason"]
            and proof.get("terrainKind") == error.get("terrainKind")
        ):
            return {"outcome": "justified_refusal", "proof": proof["kind"]}
        return {"outcome": "technical_error", "proof": "unverified_input_refusal"}
    if failure in {
        "missing_result",
        "incomplete_diagnostics",
        "evidence_error",
        "inconsistent_result",
        "process_failure",
        "process_timeout",
        "raw_identity_mismatch",
    }:
        return {"outcome": "technical_error", "proof": failure}
    if status == "success":
        audits = record.get("audits", [])
        proofs = record.get("auditScopeProofs")
        statuses = {
            research_audit_status(
                audit,
                None
                if proofs is None
                else proofs[index]
                if index < len(proofs)
                else [],
            )
            for index, audit in enumerate(audits)
        }
        if "violation" in statuses:
            return {"outcome": "invalid_route", "proof": "independent_polyline_audit"}
        if statuses == {"valid"}:
            return {"outcome": "valid_route", "proof": "independent_polyline_audit"}
        return {"outcome": "indeterminate", "proof": "incomplete_route_audit"}
    if status != "no_route" or not complete_no_route_error(record.get("error")):
        return {
            "outcome": "technical_error",
            "proof": "unsupported_or_incomplete_result",
        }
    points = scenario["points"]
    limit = scenario["routingOptions"].get("MaxRouteLengthMetres", math.inf)
    lower_bound = distance(
        (points[0]["Longitude"], points[0]["Latitude"]),
        (points[1]["Longitude"], points[1]["Latitude"]),
    )
    if lower_bound > limit:
        return {
            "outcome": "justified_refusal",
            "proof": "geodesic_lower_bound",
            "lowerBoundMetres": lower_bound,
            "limitMetres": limit,
        }
    surface = [
        event["fields"]
        for event in (record.get("diagnostics") or {}).get("events", [])
        if event.get("eventId") == 9
    ]
    direct = record.get("straightLineDiagnostic", {})
    if direct.get("status") == "valid" and within_search(record, points):
        return {
            "outcome": "confirmed_missed_route",
            "proof": "independently_audited_direct_path",
        }
    for witness in witnesses:
        if witness.get("audit", {}).get("status") == "valid" and within_search(
            record, witness.get("geometry")
        ):
            return {"outcome": "confirmed_missed_route", "proof": witness["kind"]}
    if any(attempt.get("ResourceLimited") is True for attempt in surface):
        return {"outcome": "resource_limit", "proof": "router_surface_search_budget"}
    return {
        "outcome": "indeterminate",
        "proof": "no_independent_existence_or_absence_proof",
    }


def causes(record, scenario, conclusion, *, categories=()):
    """Multiple candidate causes, with fact scope kept distinct from causation."""
    groups = []

    def add(code, owner, confidence, scope, *, root=False, evidence=None):
        groups.append(
            {
                "code": code,
                "owner": owner,
                "confidence": confidence,
                "scope": scope,
                "rootCause": root,
                "evidence": evidence or [],
            }
        )

    outcome = conclusion["outcome"]
    if outcome in ("invalid_route", "confirmed_missed_route"):
        add(
            outcome,
            "algorithm",
            "confirmed",
            "result",
            root=True,
            evidence=[conclusion["proof"]],
        )
    elif outcome == "technical_error":
        add(conclusion["proof"], "technical", "confirmed", "process", root=True)
    elif outcome == "resource_limit":
        add(conclusion["proof"], "resource", "confirmed", "execution_budget", root=True)
    elif outcome == "justified_refusal":
        add(
            conclusion["proof"],
            "model",
            "confirmed",
            "configured_limit"
            if conclusion["proof"] == "geodesic_lower_bound"
            else "placement_rule",
        )
    if any(
        proof.get("kind") == "auditor_model_rule_mismatch"
        for alternative in record.get("auditScopeProofs", [])
        for proof in alternative
    ):
        add(
            "auditor_model_rule_mismatch",
            "technical",
            "confirmed",
            "research",
            root=True,
            evidence=["independent_mesh_water_opening_proof"],
        )
    findings = record.get("straightLineDiagnostic", {}).get("findings", [])
    for reason, code in (
        ("road_contact", "direct_road_barrier"),
        ("water_without_opening", "direct_water_barrier"),
        ("blocked_terrain", "direct_blocked_terrain"),
        ("shared_road_section", "direct_audit_incomplete"),
    ):
        matches = [
            finding
            for finding in findings
            if finding.get("reason") == reason and not finding.get("allowed", False)
        ]
        if matches:
            add(
                code,
                "model" if reason != "shared_road_section" else "technical",
                "confirmed",
                "straight_line",
                evidence=sorted(
                    {
                        str(
                            f.get(
                                "sourceId",
                                f.get("featureId", f.get("roadId", "geometry")),
                            )
                        )
                        for f in matches
                    }
                ),
            )
    events = (record.get("diagnostics") or {}).get("events", [])
    if any(
        event.get("eventId") == 9
        and event.get("fields", {}).get("ResourceLimited") is True
        for event in events
    ):
        add("router_surface_budget_exhausted", "resource", "confirmed", "finite_search")
    connections = [event["fields"] for event in events if event.get("eventId") == 6]
    if record.get("status") == "no_route" and any(
        field.get("StartConnections") == 0 or field.get("DestinationConnections") == 0
        for field in connections
    ):
        add("endpoint_graph_disconnected", "algorithm", "confirmed", "finite_graph")
        add(
            "endpoint_connectivity_or_local_obstacles",
            "algorithm",
            "hypothesis",
            "search",
            root=True,
        )
    meshes = search_meshes(record.get("diagnostics")) or []
    if any(
        mesh.get("CellSizeMetres", 0) > mesh.get("RequestedCellSizeMetres", math.inf)
        for mesh in meshes
    ):
        add("adaptive_grid_coarsening", "resource", "confirmed", "finite_graph")
        if outcome == "indeterminate":
            add("grid_resolution_loss", "algorithm", "hypothesis", "search", root=True)
    if outcome == "indeterminate" and record.get("status") == "no_route":
        add(
            "detour_or_graph_connectivity_unresolved",
            "algorithm",
            "hypothesis",
            "search",
            root=True,
        )
        if {"underpass", "bridge", "water"}.intersection(categories):
            add(
                "crossing_geometry_or_mapping_gap",
                "geodata",
                "hypothesis",
                "source",
                root=True,
            )
    if "restricted" in categories:
        add("restricted_area_unmodelled", "model", "confirmed", "model")
    independent = record.get("independentResearch") or {}
    if independent.get("status") in ("limited", "resource_limit"):
        add(
            "independent_probe_limit",
            "resource",
            "confirmed",
            "research",
            evidence=[independent.get("reason")],
        )
    elif independent.get("status") == "technical_error":
        add(
            "independent_probe_failure", "technical", "confirmed", "research", root=True
        )
    return groups


def fallback_metrics(record):
    snapshot = record.get("diagnostics") or {}
    if snapshot.get("droppedEvents") != 0:
        return None
    events = snapshot.get("events", [])
    meshes = [event for event in events if event.get("eventId") == 5]
    if not meshes:
        return None
    retries = [event["fields"] for event in events if event.get("eventId") == 8]
    surface = [event["fields"] for event in events if event.get("eventId") == 9]
    return {
        "meshAttempts": len(meshes),
        "retriedMesh": len(meshes) > 1,
        "surfaceEnabledAttempts": sum(
            field.get("SurfaceRecovery") is True for field in retries
        ),
        "surfaceAttempts": len(surface),
        "surfaceFound": sum(field.get("Found") is True for field in surface),
        "surfaceResourceLimited": sum(
            field.get("ResourceLimited") is True for field in surface
        ),
    }


def aggregate(records):
    if len({record["id"] for record in records}) != len(records):
        raise ValueError("Duplicate scenario results")
    counts = Counter(record["outcome"] for record in records)
    strata = {
        name: defaultdict(Counter)
        for name in ("region", "category", "split", "distanceClass", "complexity")
    }
    grouped = {}
    for record in records:
        if record["outcome"] not in OUTCOMES:
            raise ValueError("Unknown outcome")
        for name, values in (
            ("region", [record["region"]]),
            ("category", record["categories"]),
            ("split", [record["split"]]),
            ("distanceClass", [record["distanceClass"]]),
            ("complexity", [record["complexity"]]),
        ):
            for value in set(values):
                strata[name][value][record["outcome"]] += 1
        for cause in record["causes"]:
            key = (cause["code"], cause["owner"], cause["confidence"], cause["scope"])
            group = grouped.setdefault(
                key,
                {
                    **{k: v for k, v in cause.items() if k != "evidence"},
                    "count": 0,
                    "developmentCount": 0,
                    "holdoutCount": 0,
                    "representatives": [],
                },
            )
            group["count"] += 1
            holdout = record["split"] == "holdout"
            group["holdoutCount" if holdout else "developmentCount"] += 1
            if not holdout and len(group["representatives"]) < 3:
                group["representatives"].append(record["id"])
    groups = sorted(grouped.values(), key=lambda g: (-g["developmentCount"], g["code"]))
    wall = [
        record["wallMilliseconds"]
        for record in records
        if record.get("wallMilliseconds") is not None
    ]
    rss = [
        record["peakRssBytes"]
        for record in records
        if record.get("peakRssBytes") is not None
    ]
    alternatives = [
        alternative
        for record in records
        for alternative in record.get("alternatives", [])
    ]
    fallback = [
        record["fallback"] for record in records if record.get("fallback") is not None
    ]
    backlog = []
    for group in groups:
        if not group["developmentCount"] or not group["rootCause"]:
            continue
        critical = group["code"] in ("invalid_route", "confirmed_missed_route")
        backlog.append(
            {
                "group": group["code"],
                "confidence": group["confidence"],
                "owner": group["owner"],
                "affectedDevelopmentCases": group["developmentCount"],
                "severity": "critical"
                if critical
                else "high"
                if group["owner"] == "technical"
                else "medium",
                "estimatedEffort": "high; independent witness and root-cause study required"
                if group["owner"] == "algorithm"
                else "medium; profiling and budget study"
                if group["owner"] == "resource"
                else "medium; source geometry/topology study"
                if group["owner"] == "geodata"
                else "low-to-medium; reproduce technical failure",
                "effortEstimateConfidence": "low; class-level estimate, not a diagnosed fix",
                "priority": 0
                if critical
                else 1
                if group["owner"] == "technical"
                else 2,
                "representatives": group["representatives"],
            }
        )
    backlog.sort(
        key=lambda item: (
            item["priority"],
            -item["affectedDevelopmentCases"],
            item["group"],
        )
    )
    return {
        "scenarioCount": len(records),
        "outcomes": {outcome: counts[outcome] for outcome in OUTCOMES},
        **{
            "by" + name[0].upper() + name[1:]: {
                key: {outcome: counter[outcome] for outcome in OUTCOMES}
                for key, counter in sorted(values.items())
            }
            for name, values in strata.items()
        },
        "groups": groups,
        "backlog": backlog,
        "performance": {
            "wallP50Milliseconds": percentile(wall, 0.5),
            "wallP95Milliseconds": percentile(wall),
            "sumProcessWallMilliseconds": sum(wall),
            "wallMeasuredCount": len(wall),
            "peakRssP95Bytes": percentile(rss),
            "maxPeakRssBytes": max(rss) if rss else None,
            "memoryMeasuredCount": len(rss),
            "memoryUnavailableCount": len(records) - len(rss),
        },
        "routes": {
            "alternativeCount": len(alternatives),
            "lengthP50Metres": percentile(
                [a["lengthMetres"] for a in alternatives], 0.5
            ),
            "lengthP95Metres": percentile([a["lengthMetres"] for a in alternatives]),
            "costP50": percentile([a["weightedCost"] for a in alternatives], 0.5),
            "costP95": percentile([a["weightedCost"] for a in alternatives]),
        },
        "fallback": {
            "measuredCases": len(fallback),
            "unavailableCases": len(records) - len(fallback),
            **{
                key: sum(int(item.get(key, 0)) for item in fallback)
                for key in (
                    "retriedMesh",
                    "meshAttempts",
                    "surfaceEnabledAttempts",
                    "surfaceAttempts",
                    "surfaceFound",
                    "surfaceResourceLimited",
                )
            },
        },
        "notes": [
            "Category and cause groups overlap; totals are not additive.",
            "Blocked direct segments are observations, not proof that a detour is impossible.",
            "Hypotheses are not confirmed algorithm defects; holdout never supplies backlog representatives or priority counts.",
            "Railway crossing is permitted. Restricted-area access is outside the current model.",
        ],
    }


def compare(head, baseline):
    for key in (
        "corpusSha256",
        "databaseSha256",
        "workers",
        "timeoutSeconds",
        "managedHeapBytes",
        "probeTimeoutSeconds",
        "evaluateHoldout",
        "effectiveOptionsSha256",
        "assessmentSha256",
    ):
        if key not in head["provenance"] or head["provenance"].get(key) != baseline[
            "provenance"
        ].get(key):
            raise ValueError("Incompatible comparison input: " + key)
    current = {record["id"]: record for record in head["records"]}
    previous = {record["id"]: record for record in baseline["records"]}
    if (
        set(current) != set(previous)
        or len(current) != len(head["records"])
        or len(previous) != len(baseline["records"])
    ):
        raise ValueError("Comparison scenario inventories differ")
    transitions, lost, deltas = Counter(), 0, []
    for case_id, after in current.items():
        before = previous[case_id]
        if any(
            before.get(field) != after.get(field)
            for field in (
                "split",
                "region",
                "categories",
                "distanceClass",
                "complexity",
            )
        ):
            raise ValueError("Comparison scenario metadata differ")
        transitions[before["outcome"] + " -> " + after["outcome"]] += 1
        lost += before["outcome"] == "valid_route" and after["outcome"] != "valid_route"
        if before["outcome"] == after["outcome"] == "valid_route":
            old = {a["profile"]: a for a in before.get("alternatives", [])}
            new = {a["profile"]: a for a in after.get("alternatives", [])}
            for profile in sorted(set(old) & set(new)):
                deltas.append(
                    {
                        "lengthMetres": new[profile]["lengthMetres"]
                        - old[profile]["lengthMetres"],
                        "weightedCost": new[profile]["weightedCost"]
                        - old[profile]["weightedCost"],
                    }
                )
    comparable = all(
        head["provenance"].get(key)
        and head["provenance"][key] == baseline["provenance"].get(key)
        for key in ("platform", "processor", "logicalCpuCount")
    )

    def ratio(field):
        after = [
            record[field]
            for record in current.values()
            if record.get(field) is not None
        ]
        before = [
            record[field]
            for record in previous.values()
            if record.get(field) is not None
        ]
        if not comparable or len(after) != len(current) or len(before) != len(previous):
            return None
        denominator = percentile(before)
        return round(percentile(after) / denominator, 4) if denominator else None

    return {
        "lostValidRoutes": lost,
        "performanceComparable": comparable,
        "wallP95Ratio": ratio("wallMilliseconds"),
        "peakRssP95Ratio": ratio("peakRssBytes"),
        "outcomeTransitions": dict(sorted(transitions.items())),
        "comparedAlternatives": len(deltas),
        "lengthDeltaP50Metres": percentile(
            [delta["lengthMetres"] for delta in deltas], 0.5
        ),
        "costDeltaP50": percentile([delta["weightedCost"] for delta in deltas], 0.5),
        "baselineAlgorithm": baseline["provenance"].get("algorithmSha256"),
        "currentAlgorithm": head["provenance"].get("algorithmSha256"),
    }


def markdown(report):
    summary = report["summary"]
    lines = [
        "# Belarus routing research",
        "",
        f"Evidence gate: **{'PASS' if report.get('gatePassed') else 'FAIL'}**; cases: {summary['scenarioCount']}.",
        f"Quality gate: **{'PASS' if report.get('qualityGatePassed') else 'FAIL'}** (new quality findings are recorded as baseline discoveries)."
        if "qualityGatePassed" in report
        else "Quality gate: not evaluated until the experiment is complete.",
        "",
        "NoRoute is not proof of impossibility. Railway crossings are allowed; restricted land is a model limitation.",
        "",
        "| Outcome | Count |",
        "| --- | ---: |",
    ]
    lines.extend(
        f"| {outcome} | {count} |" for outcome, count in summary["outcomes"].items()
    )
    for label in (
        "byRegion",
        "bySplit",
        "byCategory",
        "byDistanceClass",
        "byComplexity",
    ):
        lines += [
            "",
            f"## {label}",
            "",
            "| Stratum | Cases | Valid | Invalid | Missed | Refusal | Unknown | Resource | Error |",
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
        ]
        for name, counts in summary[label].items():
            lines.append(
                f"| {name} | {sum(counts.values())} | "
                + " | ".join(str(counts[key]) for key in OUTCOMES)
                + " |"
            )
    performance = summary["performance"]
    lines += [
        "",
        f"Cold process wall P95: {performance['wallP95Milliseconds']} ms; peak RSS P95: {performance['peakRssP95Bytes']} bytes; memory measured: {performance['memoryMeasuredCount']}/{summary['scenarioCount']}.",
        "",
        f"Routes: {summary['routes']}",
        "",
        f"Fallback diagnostics: {summary['fallback']}",
        "",
        "## Groups (overlapping; development representatives only)",
        "",
        "| Group | Owner | Confidence/scope | Dev | Holdout | Representatives |",
        "| --- | --- | --- | ---: | ---: | --- |",
    ]
    for group in summary["groups"][:20]:
        lines.append(
            f"| {group['code']} | {group['owner']} | {group['confidence']}/{group['scope']} | {group['developmentCount']} | {group['holdoutCount']} | {', '.join(group['representatives'])} |"
        )
    lines += ["", "## Follow-up priorities (holdout excluded)", ""]
    for item in summary["backlog"][:8]:
        lines.append(
            f"- P{item['priority']} {item['group']}: {item['affectedDevelopmentCases']} cases; {item['severity']}; {item['confidence']}; effort: {item['estimatedEffort']}."
        )
    lines += [
        "",
        "Full geometry, invocation, process output, diagnostic events and independent audits are separate artifacts. Holdout details remain in the sealed evaluation artifact; do not use them for fixes.",
        "",
    ]
    if report.get("comparison"):
        lines += [f"Version comparison: {report['comparison']}", ""]
    if report.get("fatalError"):
        lines += [f"Incomplete experiment: {report['fatalError']}", ""]
    return "\n".join(lines)
