# Synthetic issue index (complete)

These are invented local references, not fetched issues. There are no other
open or closed issues in this snapshot. No fixes below have been applied.

LS-11 [open bug]: due_order uses title as its sort key rather than due. With
{id: 1, title: Zebra, due: 2031-01-01, archived: false} and
{id: 2, title: Apple, due: 2031-02-01, archived: false}, it yields IDs 2,1,
but the documented date order is 1,2. Scope: src/shelf.py due_order only.

LS-12 [open feature]: add a pure function export_titles(entries) returning active
titles, one per line in input order. No files or network. Scope: src/shelf.py.

LS-13 [closed, no change]: a reporter expected search to be case-sensitive; the
contract intentionally says case-insensitive. It does not concern renaming.

No existing issue covers archive modifying unrelated IDs or access.can_edit.
