"""Check resolved Gradle runtime coordinates against OSV without sending source code."""
import json
import re
import sys
import urllib.request
from pathlib import Path

dependency_text = Path(sys.argv[1]).read_text(encoding="utf-8-sig")
coordinates = set()
for line in dependency_text.splitlines():
    match = re.search(r"--- ([\w.\-]+):([\w.\-]+):([^\s]+)(?: -> ([^\s]+))?", line)
    if not match:
        continue
    group, artifact, version, selected = match.groups()
    if selected:
        version = selected.rsplit(":", 1)[-1]
    if version[0].isdigit():
        coordinates.add((f"{group}:{artifact}", version))
coordinates = sorted(coordinates)
results = []
for offset in range(0, len(coordinates), 50):
    batch = coordinates[offset:offset + 50]
    payload = {"queries": [{"package": {"ecosystem": "Maven", "name": name}, "version": version} for name, version in batch]}
    request = urllib.request.Request("https://api.osv.dev/v1/querybatch", json.dumps(payload).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=60) as response:
        data = json.load(response)
    for (name, version), finding in zip(batch, data["results"]):
        results.append({"name": name, "version": version, "advisories": [item["id"] for item in finding.get("vulns", [])]})
Path(sys.argv[2]).write_text(json.dumps(results, indent=2), encoding="utf-8")
affected = [item for item in results if item["advisories"]]
print(json.dumps({"packages_checked": len(results), "affected": affected}, indent=2))
sys.exit(bool(affected))
