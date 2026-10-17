# Worked examples

Input A: [{"id": 1, "title": "Zebra", "due": "2031-01-01", "archived": false},
          {"id": 2, "title": "Apple", "due": "2031-02-01", "archived": false}].
Expected due_order(A) IDs: 1,2. Current source instead sorts titles.
Expected archive(A, 1): only ID 1 archived. Current source archives both.
Expected rename(A, 1, "Cedar"): ID 1 title Cedar, ID 2 title Apple.
Expected find_title(A, "zEb"): ID 1. Archived entries are excluded.
Expected can_edit("viewer"): false; can_edit("editor"): true.

Repository code does not model sync, accounts, operating systems, storage, a UI,
a clock or activity telemetry. Vague observations in those areas cannot identify
an apparent code fix without more details.
