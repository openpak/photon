from parse_oplog import label_records


def test_labels_opcodes_from_constant_map():
    records = [
        {"t": "2026-05-29T00:00:00Z", "kind": "response", "payload": {"code": 230}},
        {"t": "2026-05-29T00:00:01Z", "kind": "event", "payload": {"code": 255}},
    ]
    op_names = {"OperationCode": {230: "Authenticate"}, "EventCode": {255: "Join"}}
    out = label_records(records, op_names)
    assert out[0]["label"] == "Authenticate"   # response -> OperationCode
    assert out[1]["label"] == "Join"           # event -> EventCode


def test_unknown_code_falls_back_to_group_and_number():
    records = [{"t": "x", "kind": "send", "payload": {"code": 99}}]
    out = label_records(records, {"OperationCode": {}})
    assert out[0]["label"] == "OperationCode:99"
