# Belarus routing research

Evidence gate: **PASS**; cases: 300.
Quality gate: **PASS** (new quality findings are recorded as baseline discoveries).

NoRoute is not proof of impossibility. Railway crossings are allowed; restricted land is a model limitation.

| Outcome | Count |
| --- | ---: |
| valid_route | 97 |
| invalid_route | 0 |
| confirmed_missed_route | 0 |
| justified_refusal | 0 |
| indeterminate | 98 |
| resource_limit | 105 |
| technical_error | 0 |

## byRegion

| Stratum | Cases | Valid | Invalid | Missed | Refusal | Unknown | Resource | Error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| BY-BR | 43 | 11 | 0 | 0 | 0 | 14 | 18 | 0 |
| BY-HM | 32 | 3 | 0 | 0 | 0 | 2 | 27 | 0 |
| BY-HO | 43 | 20 | 0 | 0 | 0 | 15 | 8 | 0 |
| BY-HR | 30 | 8 | 0 | 0 | 0 | 14 | 8 | 0 |
| BY-MA | 30 | 12 | 0 | 0 | 0 | 10 | 8 | 0 |
| BY-MI | 79 | 31 | 0 | 0 | 0 | 29 | 19 | 0 |
| BY-VI | 43 | 12 | 0 | 0 | 0 | 14 | 17 | 0 |

## bySplit

| Stratum | Cases | Valid | Invalid | Missed | Refusal | Unknown | Resource | Error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| development | 170 | 48 | 0 | 0 | 0 | 60 | 62 | 0 |
| frozen | 88 | 39 | 0 | 0 | 0 | 23 | 26 | 0 |
| holdout | 42 | 10 | 0 | 0 | 0 | 15 | 17 | 0 |

## byCategory

| Stratum | Cases | Valid | Invalid | Missed | Refusal | Unknown | Resource | Error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| bridge | 66 | 25 | 0 | 0 | 0 | 19 | 22 | 0 |
| countryside | 17 | 9 | 0 | 0 | 0 | 3 | 5 | 0 |
| dense-buildings | 18 | 1 | 0 | 0 | 0 | 8 | 9 | 0 |
| field | 72 | 33 | 0 | 0 | 0 | 19 | 20 | 0 |
| forest | 69 | 27 | 0 | 0 | 0 | 22 | 20 | 0 |
| interchange | 11 | 2 | 0 | 0 | 0 | 3 | 6 | 0 |
| legacy-A | 32 | 14 | 0 | 0 | 0 | 10 | 8 | 0 |
| legacy-B | 23 | 7 | 0 | 0 | 0 | 6 | 10 | 0 |
| legacy-E | 19 | 4 | 0 | 0 | 0 | 7 | 8 | 0 |
| legacy-F | 14 | 14 | 0 | 0 | 0 | 0 | 0 | 0 |
| marsh | 11 | 2 | 0 | 0 | 0 | 7 | 2 | 0 |
| multi | 122 | 18 | 0 | 0 | 0 | 46 | 58 | 0 |
| narrow-gap | 18 | 2 | 0 | 0 | 0 | 8 | 8 | 0 |
| railway | 10 | 4 | 0 | 0 | 0 | 0 | 6 | 0 |
| restricted | 9 | 4 | 0 | 0 | 0 | 1 | 4 | 0 |
| road | 88 | 39 | 0 | 0 | 0 | 23 | 26 | 0 |
| road-local | 24 | 2 | 0 | 0 | 0 | 17 | 5 | 0 |
| road-major | 23 | 5 | 0 | 0 | 0 | 5 | 13 | 0 |
| road-secondary | 21 | 4 | 0 | 0 | 0 | 5 | 12 | 0 |
| roundabout | 17 | 1 | 0 | 0 | 0 | 2 | 14 | 0 |
| station | 9 | 1 | 0 | 0 | 0 | 6 | 2 | 0 |
| underpass | 10 | 1 | 0 | 0 | 0 | 1 | 8 | 0 |
| water | 10 | 5 | 0 | 0 | 0 | 4 | 1 | 0 |

## byDistanceClass

| Stratum | Cases | Valid | Invalid | Missed | Refusal | Unknown | Resource | Error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| full | 4 | 0 | 0 | 0 | 0 | 1 | 3 | 0 |
| local | 205 | 83 | 0 | 0 | 0 | 53 | 69 | 0 |
| long | 32 | 4 | 0 | 0 | 0 | 13 | 15 | 0 |
| medium | 59 | 10 | 0 | 0 | 0 | 31 | 18 | 0 |

## byComplexity

| Stratum | Cases | Valid | Invalid | Missed | Refusal | Unknown | Resource | Error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| combined | 107 | 17 | 0 | 0 | 0 | 41 | 49 | 0 |
| dense | 18 | 1 | 0 | 0 | 0 | 8 | 9 | 0 |
| legacy | 70 | 35 | 0 | 0 | 0 | 16 | 19 | 0 |
| single | 105 | 44 | 0 | 0 | 0 | 33 | 28 | 0 |

Cold process wall P95: 52075.75 ms; peak RSS P95: 182582476.8 bytes; memory measured: 298/300.

Routes: {'alternativeCount': 164, 'lengthP50Metres': 779.461, 'lengthP95Metres': 4426.02, 'costP50': 1095.136, 'costP95': 7487.298}

Fallback diagnostics: {'measuredCases': 298, 'unavailableCases': 2, 'retriedMesh': 212, 'meshAttempts': 914, 'surfaceEnabledAttempts': 616, 'surfaceAttempts': 1848, 'surfaceFound': 33, 'surfaceResourceLimited': 519}

## Groups (overlapping; development representatives only)

| Group | Owner | Confidence/scope | Dev | Holdout | Representatives |
| --- | --- | --- | ---: | ---: | --- |
| adaptive_grid_coarsening | resource | confirmed/finite_graph | 181 | 31 | naliboki-02, pukhovichi-01, pukhovichi-02 |
| direct_road_barrier | model | confirmed/straight_line | 165 | 29 | naliboki-02, pukhovichi-01, pukhovichi-02 |
| direct_blocked_terrain | model | confirmed/straight_line | 89 | 19 | pukhovichi-03, mass-naliboki-bridge-01, mass-naliboki-bridge-05 |
| router_surface_budget_exhausted | resource | confirmed/finite_search | 87 | 16 | pukhovichi-02, pukhovichi-03, smolevichi-01 |
| router_surface_search_budget | resource | confirmed/execution_budget | 87 | 16 | pukhovichi-02, pukhovichi-03, smolevichi-01 |
| detour_or_graph_connectivity_unresolved | algorithm | hypothesis/search | 82 | 15 | naliboki-02, pukhovichi-01, mass-naliboki-bridge-01 |
| grid_resolution_loss | algorithm | hypothesis/search | 82 | 15 | naliboki-02, pukhovichi-01, mass-naliboki-bridge-01 |
| direct_water_barrier | model | confirmed/straight_line | 72 | 15 | naliboki-02, pukhovichi-01, pukhovichi-02 |
| crossing_geometry_or_mapping_gap | geodata | hypothesis/source | 20 | 3 | mass-naliboki-bridge-01, mass-naliboki-bridge-03, mass-naliboki-bridge-04 |
| restricted_area_unmodelled | model | confirmed/model | 7 | 2 | research-by-br-way-239728593, research-by-hm-way-363127232, research-by-ho-way-609709413 |
| endpoint_connectivity_or_local_obstacles | algorithm | hypothesis/search | 6 | 2 | research-by-hm-way-1436509658, research-by-hm-way-173374041, research-by-hm-way-253582570 |
| endpoint_graph_disconnected | algorithm | confirmed/finite_graph | 6 | 2 | research-by-hm-way-1436509658, research-by-hm-way-173374041, research-by-hm-way-253582570 |
| auditor_model_rule_mismatch | technical | confirmed/research | 1 | 0 | research-by-hm-way-939978585 |
| process_time_budget | resource | confirmed/execution_budget | 1 | 1 | research-by-vi-way-1318455954 |

## Follow-up priorities (holdout excluded)

- P1 auditor_model_rule_mismatch: 1 cases; high; confirmed; effort: low-to-medium; reproduce technical failure.
- P2 router_surface_search_budget: 87 cases; medium; confirmed; effort: medium; profiling and budget study.
- P2 detour_or_graph_connectivity_unresolved: 82 cases; medium; hypothesis; effort: high; independent witness and root-cause study required.
- P2 grid_resolution_loss: 82 cases; medium; hypothesis; effort: high; independent witness and root-cause study required.
- P2 crossing_geometry_or_mapping_gap: 20 cases; medium; hypothesis; effort: medium; source geometry/topology study.
- P2 endpoint_connectivity_or_local_obstacles: 6 cases; medium; hypothesis; effort: high; independent witness and root-cause study required.
- P2 process_time_budget: 1 cases; medium; confirmed; effort: medium; profiling and budget study.

Full geometry, invocation, process output, diagnostic events and independent audits are separate artifacts. Holdout details remain in the sealed evaluation artifact; do not use them for fixes.
