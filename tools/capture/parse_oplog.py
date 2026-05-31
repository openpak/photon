"""Label and summarize the BepInEx op-logger JSONL into a readable timeline."""
import csv
import json
import sys
from pathlib import Path


def load_constant_map(csv_path: Path) -> dict:
    """photon_constants.csv (Group,Name,Value) -> {Group: {int_value: Name}}."""
    out: dict[str, dict[int, str]] = {}
    with open(csv_path, newline="") as f:
        for row in csv.DictReader(f):
            try:
                value = int(row["Value"])
            except (ValueError, KeyError):
                continue
            out.setdefault(row["Group"], {})[value] = row["Name"]
    return out


def label_records(records: list[dict], op_names: dict) -> list[dict]:
    """Attach a human label to each record by joining its numeric code to the right group."""
    group_for_kind = {"response": "OperationCode", "send": "OperationCode", "event": "EventCode"}
    labelled = []
    for r in records:
        group = group_for_kind.get(r.get("kind"), "")
        code = (r.get("payload") or {}).get("code")
        label = op_names.get(group, {}).get(code, f"{group}:{code}")
        labelled.append({**r, "label": label})
    return labelled


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: parse_oplog.py <oplog.jsonl> <photon_constants.csv>")
        return 2
    records = [json.loads(line) for line in Path(sys.argv[1]).read_text().splitlines() if line.strip()]
    op_names = load_constant_map(Path(sys.argv[2]))
    for r in label_records(records, op_names):
        print(f"{r['t']}  {r['kind']:<9} {r['label']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
