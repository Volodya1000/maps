"""Summarize the frozen Belarus crossing run without discarding failures."""

import argparse
import hashlib
import json
import math
from collections import Counter, defaultdict
from pathlib import Path

from routing_crossing_pilot import audit, distance, harness_integrity, project
from routing_indirect_study import requested_search_bounds
from shapely.geometry import LineString, Polygon, box, shape

ROOT = Path(__file__).resolve().parents[1]


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    return round(
        ordered[lower]
        + (ordered[math.ceil(position)] - ordered[lower]) * (position - lower)
    )


def witness_status(folder, scenario, coverage):
    if not scenario.get("witness") and not (folder / "witness.geojson").exists():
        return "absent"
    path = folder / "witness.geojson"
    if path.exists():
        geometry = shape(json.loads(path.read_text())["features"][0]["geometry"])
    elif scenario["witness"].get("coordinates"):
        geometry = LineString(scenario["witness"]["coordinates"])
    else:
        return "unknown"
    if geometry.geom_type != "LineString" or len(geometry.coords) < 2:
        return "invalid"
    points = [(item["Longitude"], item["Latitude"]) for item in scenario["points"]]
    if (
        len(points) != 2
        or distance(geometry.coords[0], points[0]) >= 1
        or distance(geometry.coords[-1], points[1]) >= 1
        or not box(*requested_search_bounds(scenario["points"])).covers(geometry)
    ):
        return "invalid"
    raw = json.loads((folder / "input.geojson").read_text())["features"]
    features = [
        {
            "id": item["properties"]["featureId"],
            "kind": item["properties"]["kind"],
            "geometry": shape(item["geometry"]),
            "approval_id": item["properties"].get("approvalId"),
        }
        for item in raw
        if "featureId" in item["properties"]
    ]
    origin = (scenario["points"][0]["Longitude"], scenario["points"][0]["Latitude"])
    return audit(
        project(geometry, origin),
        features,
        coverage=project(coverage, origin),
        origin=origin,
    )["status"]


def summarize(run_dir, inventory_path, output):
    frozen = ROOT / "samples/routing-crossing-mass-v1.json"
    if frozen.read_bytes() != (run_dir / "scenarios.json").read_bytes():
        raise ValueError("run does not match frozen corpus")
    scenarios = json.loads(frozen.read_text())
    inventory = json.loads(inventory_path.read_text())
    coverage = Polygon(json.loads((ROOT / "data/belarus-coverage.json").read_text()))
    records = []
    raw_results = []
    for scenario in scenarios:
        folder = run_dir / scenario["id"]
        result = json.loads((folder / "result.json").read_text())
        if (
            result.get("id") != scenario["id"]
            or result.get("databaseSha256") != scenario["databaseSha256"]
            or result.get("databaseSha256") != inventory["databaseSha256"]
        ):
            raise ValueError("result identity or database provenance mismatch")
        raw_results.append(result)
        crossing_types = set()
        road_ids = set()
        for alternative in result["audit"]["alternatives"]:
            for finding in alternative["findings"]:
                if finding["reason"] == "road_contact" and finding.get("allowed"):
                    road_ids.add(finding["roadId"])
                    if finding["bridgeIds"]:
                        crossing_types.add("bridge")
                    if finding["gasIds"]:
                        crossing_types.add("overhead_gas")
        witness = witness_status(folder, scenario, coverage)
        record = {
            "id": scenario["id"],
            "area": scenario["area"],
            "category": scenario["category"],
            "scenarioType": scenario.get("scenarioType", "pilot"),
            "points": scenario["points"],
            "targetRoad": scenario["targetRoad"],
            "candidateIds": scenario["candidateIds"],
            "status": result["status"],
            "typedReason": (result.get("error") or {}).get("reason"),
            "searchFailures": (result.get("error") or {}).get("searchFailures", []),
            "alternativeCount": len(result["alternatives"]),
            "auditStatus": result["audit"]["status"],
            "crossingTypesUsed": sorted(crossing_types),
            "crossedRoadIds": sorted(road_ids),
            "wallMilliseconds": result.get("wallMilliseconds"),
            "routerMilliseconds": result.get("elapsedMilliseconds"),
            "sourceDirty": result.get("sourceDirty"),
            "verifyDllSha256": result.get("verifyDllSha256"),
            "peakRssBytes": None,
            "effectiveCellMetres": None,
            "witnessStatus": witness,
            "witnessKind": (scenario.get("witness") or {}).get("kind")
            or ("direct" if (folder / "witness.geojson").exists() else None),
        }
        records.append(record)
    if not harness_integrity(scenarios, raw_results):
        raise ValueError("incomplete or inconsistent run")
    app_commits = {item.get("appCommit") for item in raw_results}
    dll_hashes = {item.get("verifyDllSha256") for item in raw_results}
    if (
        len(app_commits) != 1
        or not next(iter(app_commits))
        or len(dll_hashes) != 1
        or not next(iter(dll_hashes))
    ):
        raise ValueError("mixed application or Verify DLL provenance")
    counts = Counter(item["status"] for item in records)
    valid = [
        item
        for item in records
        if item["status"] == "success" and item["auditStatus"] == "valid"
    ]
    positive = [item for item in records if item["witnessStatus"] == "valid"]
    missed = [item for item in positive if item["status"] == "no_route"]
    times = [
        item["wallMilliseconds"]
        for item in records
        if item["wallMilliseconds"] is not None
    ]
    grouped = defaultdict(Counter)
    for item in records:
        grouped[f"area:{item['area']}"][item["status"]] += 1
        grouped[f"category:{item['category']}"][item["status"]] += 1
        grouped[f"type:{item['scenarioType']}"][item["status"]] += 1
    metrics = {
        "scenarioCount": len(records),
        "sourceDirtyCaseCount": sum(item["sourceDirty"] is True for item in records),
        "validRouteCount": len(valid),
        "validRouteRate": len(valid) / len(records),
        "noRouteCount": counts["no_route"],
        "noRouteRate": counts["no_route"] / len(records),
        "domainViolationCount": sum(
            item["auditStatus"] == "violation" for item in records
        ),
        "domainViolationRate": sum(
            item["auditStatus"] == "violation" for item in records
        )
        / len(records),
        "verifiedPositiveCount": len(positive),
        "verifiedPositiveFound": sum(
            item["id"] in {v["id"] for v in valid} for item in positive
        ),
        "verifiedPositiveRecall": (
            sum(item["id"] in {v["id"] for v in valid} for item in positive)
            / len(positive)
            if positive
            else None
        ),
        "confirmedMissedValidPathCount": len(missed),
        "crossingUtilization": (
            sum(bool(item["crossingTypesUsed"]) for item in valid) / len(valid)
            if valid
            else None
        ),
        "p50WallMilliseconds": percentile(times, 0.5),
        "p95WallMilliseconds": percentile(times, 0.95),
        "statusCounts": dict(counts),
        "typedReasonCounts": dict(
            Counter(item["typedReason"] for item in records if item["typedReason"])
        ),
        "crossingTypeCounts": dict(
            Counter(kind for item in valid for kind in item["crossingTypesUsed"])
        ),
        "byAreaAndType": {key: dict(value) for key, value in sorted(grouped.items())},
    }
    data = {
        "schema": "maps.routing-crossing-mass.v1",
        "baselineAppCommit": next(iter(app_commits)),
        "verifyDllSha256": next(iter(dll_hashes)),
        "corpusSha256": hashlib.sha256(frozen.read_bytes()).hexdigest(),
        "databaseSha256": inventory["databaseSha256"],
        "sourceSha256": inventory["sourceSha256"],
        "metrics": metrics,
        "results": records,
    }
    output.mkdir(parents=True, exist_ok=True)
    (output / "results.json").write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n"
    )
    lines = [
        "# Массовый baseline маршрутизации Беларуси",
        "",
        f"Зафиксировано и выполнено {len(records)} пар в шести районах: 10 существующих пилотных и {len(records) - 10} новых. Каждая пара содержит две пользовательские точки. Целевая выборка проверяет барьеры и не оценивает случайную поездку по стране.",
        "",
        "| Показатель | Значение |",
        "| --- | ---: |",
    ]
    for label, value in (
        (
            "Допустимый маршрут",
            f"{len(valid)}/{len(records)} ({metrics['validRouteRate']:.1%})",
        ),
        (
            "NoRoute",
            f"{counts['no_route']}/{len(records)} ({metrics['noRouteRate']:.1%})",
        ),
        ("Нарушение правил", f"{metrics['domainViolationCount']}/{len(records)}"),
        (
            "Подтверждённый путь найден",
            f"{metrics['verifiedPositiveFound']}/{len(positive)}",
        ),
        ("Подтверждённый путь пропущен", str(len(missed))),
        (
            "Использование переходов среди допустимых маршрутов",
            f"{metrics['crossingUtilization']:.1%}"
            if metrics["crossingUtilization"] is not None
            else "unknown",
        ),
        (
            "Время p50 / p95",
            f"{metrics['p50WallMilliseconds']} / {metrics['p95WallMilliseconds']} мс",
        ),
    ):
        lines.append(f"| {label} | {value} |")
    lines += [
        "",
        "## Классификация",
        "",
        f"Типизированные причины: `{json.dumps(metrics['typedReasonCounts'], ensure_ascii=False)}`. NoRoute без проверенного witness остаётся неопределённым, а не дефектом A*.",
        f"Успешно использованные переходы: `{json.dumps(metrics['crossingTypeCounts'], ensure_ascii=False)}`. Числа обозначают сценарии, где аудит нашёл соответствующий контакт.",
        "",
        "| Группа | Результаты |",
        "| --- | --- |",
    ]
    for key, value in metrics["byAreaAndType"].items():
        lines.append(f"| {key} | {json.dumps(value, ensure_ascii=False)} |")
    lines += [
        "",
        "## Исходные OSM и подготовленная база",
        "",
        "Сверены закреплённые PBF и SQLite по SHA-256 ([машинный инвентарь](source-inventory.json)). Категории OSM ways пересекаются и не означают разрешение кабельных работ.",
        "",
        "| Тип | OSM ways | Найдено в подготовленных слоях |",
        "| --- | ---: | --- |",
    ]
    source = inventory["osmWays"]
    imported = inventory["importedByCategoryAndKind"]
    for kind in (
        "road_bridge",
        "pedestrian_bridge",
        "railway_bridge",
        "road_tunnel",
        "pedestrian_tunnel",
        "railway_tunnel",
        "gas_pipeline_overhead",
        "gas_pipeline_other",
        "bridge_structure",
        "covered_highway",
        "nonzero_layer",
        "ford",
    ):
        present = (
            ", ".join(
                f"{count} {key.split(':', 1)[1]}"
                for key, count in sorted(imported.items())
                if key.startswith(kind + ":")
            )
            or "0 в сравниваемых слоях"
        )
        lines.append(f"| {kind} | {source.get(kind, 0)} | {present} |")
    lines += [
        "",
        "В SQLite: 15 123 BridgeCandidate, 67 OverheadGasPipeline и 12 порождённых GasPipelineCrossingCandidate. Числа в третьем столбце показывают присутствие исходного way в указанном слое, а не пригодное пересечение дороги. Два pedestrian_tunnel с BridgeCandidate имеют также мостовой тег: отдельной поддержки тоннелей нет. `bridge=*` на highway импортируется, включая пешеходные мосты; железнодорожные мосты, самостоятельные `man_made=bridge`, `layer=*` и иные газопроводы не становятся переходами автоматически. Часть дорожных тоннелей хранится как Road. Нужна отдельная проверка геометрии пересечения дорог и предметных правил перед расширением Domain.",
        "",
        "## Доказательные случаи и следующие изменения",
        "",
        "- [Пуховичи, газовый переход](cases/pukhovichi-01/diagnostic.json): кандидат `4605961` пересекает целевую дорогу и лежит в области поиска, но прямая даёт девять запрещённых дорожных контактов и пересекает воду; `NoRouteInSearchArea`, существование обхода не доказано. [Геометрия](cases/pukhovichi-01/geometry.geojson), [карта](cases/pukhovichi-01/map.svg).",
        "- [Налибоки, мост](cases/mass-naliboki-bridge-01/diagnostic.json): кандидат `438476` пересекает целевую дорогу в области поиска, в 66 м от прямой. Прямая также встречает воду и болото; witness нет. [Геометрия](cases/mass-naliboki-bridge-01/geometry.geojson), [карта](cases/mass-naliboki-bridge-01/map.svg).",
        "- [Брест, последовательные барьеры](cases/mass-brest-multi-06/diagnostic.json): прямая даёт 10 дорожных контактов и касается запрещённой местности; модельный кандидат для целевой дороги не выбран. Отказ поиска не доказывает дефект A*. [Геометрия](cases/mass-brest-multi-06/geometry.geojson), [карта](cases/mass-brest-multi-06/map.svg).",
        "",
        "Новые сценарии A отобраны по пересечению геометрии кандидата и Road; пригодность контакта и подключение к графу не доказаны. Число реальных пересечений дорог для неподдерживаемых OSM-типов пока unknown; приведённые числа ways задают верхнюю границу для дальнейшей проверки.",
        "",
        "Первый приоритет: независимо построить и проверить witness для одного из 21 неуспеха в новой мостовой страте, затем проверить фактический шаг сетки, портал и его соединители. Это отделит дефект поиска от отсутствия допустимого пути. Следом проверить 16 неуспехов с последовательными барьерами. Для расширения покрытия данных отдельно сопоставить геометрию пешеходных тоннелей, железнодорожных мостов и иных сооружений с дорогами и получить предметное разрешение; OSM-теги сами по себе разрешения не дают. Подтверждённого локального дефекта в этом baseline нет, поэтому production-код не менялся и сравнение «до/после» неприменимо.",
        "",
        "План составлял 100 пар; фактически 88. Все 12 недобранных слотов приходятся на газовую страту: в шести выбранных районах не удалось подобрать дополнительные пары с известным типом местности и допустимыми конечными точками. Ни один неуспех не исключён после заморозки.",
        "",
        "## Воспроизводимость и пределы",
        "",
        f"Корпус SHA-256 `{data['corpusSha256']}`; SQLite `{data['databaseSha256']}`; PBF `{data['sourceSha256']}`; приложение `{data['baselineAppCommit']}`.",
        "Воспроизведение из корня репозитория после установки `uv` и .NET SDK из `global.json`:",
        "```sh",
        "uv sync --project tools/GeodataPreparation --locked",
        "uv run --project tools/GeodataPreparation --locked python tools/GeodataPreparation/country_data.py ensure --output artifacts/belarus",
        "uv run --project tools/GeodataPreparation --locked python tools/ci/routing_pilot_ci.py extract-db --data artifacts/belarus",
        "dotnet build tools/CableRoutePlanner.Verify -c Release -warnaserror",
        "uv run --project tools/GeodataPreparation --locked python tools/routing_mass_experiment.py freeze --data artifacts/belarus --output artifacts/routing-mass",
        "uv run --project tools/GeodataPreparation --locked python tools/routing_mass_experiment.py run --data artifacts/belarus --verify-dll tools/CableRoutePlanner.Verify/bin/Release/net8.0/CableRoutePlanner.Verify.dll --output artifacts/routing-mass",
        "uv run --project tools/GeodataPreparation --locked python tools/GeodataPreparation/country_data.py source --output artifacts/osm/belarus-261030.osm.pbf",
        "uv run --project tools/GeodataPreparation --locked python tools/routing_source_inventory.py --pbf artifacts/osm/belarus-261030.osm.pbf --data artifacts/belarus --output artifacts/routing-source-inventory.json",
        "mkdir -p artifacts/routing-mass-report && cp artifacts/routing-source-inventory.json artifacts/routing-mass-report/source-inventory.json",
        "uv run --project tools/GeodataPreparation --locked python tools/routing_mass_diagnostics.py --run artifacts/routing-mass --output artifacts/routing-mass-report/cases mass-naliboki-bridge-01 pukhovichi-01 mass-brest-multi-06",
        "uv run --project tools/GeodataPreparation --locked python tools/routing_mass_report.py --run artifacts/routing-mass --inventory artifacts/routing-source-inventory.json --output artifacts/routing-mass-report",
        "```",
        f"Один и тот же Verify DLL SHA-256 `{data['verifyDllSha256']}` использован во всех 88 запусках. `sourceDirty=true` записано в {metrics['sourceDirtyCaseCount']} результатах после изменения workflow во время запуска; бинарник и commit приложения не менялись. Поле сохранено для каждого случая в JSON.",
        "[Все случаи, координаты, аудит и причины](results.json) сохранены в машинном виде. Полные GeoJSON, SVG, stdout/stderr и результаты каждого процесса остаются в artifacts запуска.",
        "Пиковый RSS и фактический шаг сетки не инструментированы и записаны как null. Инженерная пригодность переходов не подтверждалась. © OpenStreetMap contributors, ODbL 1.0.",
    ]
    (output / "report.md").write_text("\n".join(lines) + "\n")
    print(json.dumps(metrics, ensure_ascii=False, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    summarize(args.run, args.inventory, args.output)


if __name__ == "__main__":
    main()
