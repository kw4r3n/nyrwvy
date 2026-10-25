#!/usr/bin/env python3
"""Summarize repeated operations without summing inclusive phase counters."""
import collections
import json
import pathlib
import statistics
import sys


def summarize(path):
    groups = collections.defaultdict(list)
    for line in pathlib.Path(path).read_text().splitlines():
        row = json.loads(line)
        if row["Sample"] >= 0:
            groups[row["Copies"], row["Format"], row["Operation"]].append(row)
    results = []
    for (copies, format_name, operation), rows in sorted(groups.items()):
        result = {"copies": copies, "format": format_name, "operation": operation, "samples": len(rows)}
        keys = ["ElapsedMs", "AllocatedBytes", "Gen0", "Gen1", "Gen2"]
        keys += [key for key in ("ProcessAllocatedBytes", "ProcessCpuMs", "GCPauseMs") if all(key in row for row in rows)]
        for key in keys:
            result[key] = statistics.median(row[key] for row in rows)
        result["MinMs"] = min(row["ElapsedMs"] for row in rows)
        result["MaxMs"] = max(row["ElapsedMs"] for row in rows)
        for source in ("Scope", "Phases"):
            names = sorted({m["Name"] for row in rows for m in row[source]})
            result[source] = []
            for name in names:
                measurements = [next((m for m in row[source] if m["Name"] == name), {}) for row in rows]
                result[source].append({"Name": name, **{
                    key: statistics.median(m.get(key, 0) for m in measurements)
                    for key in ("Computations", "CacheHits", "ElapsedTicks", "AllocatedBytes")
                }})
        results.append(result)
    return results


if __name__ == "__main__":
    print(json.dumps(summarize(sys.argv[1]), indent=2))
