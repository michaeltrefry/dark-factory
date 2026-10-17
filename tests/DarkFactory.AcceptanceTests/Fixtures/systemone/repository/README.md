# Lantern Shelf (synthetic repository)

A small, in-memory reading-list library. All names, issue references, examples and
code here were independently hand-authored for a held-out evaluation. No remote,
credentials, dependencies, hooks, settings, install steps or network operations.
Read the source; triage does not require executing anything.

Public contract: src/shelf.py accepts dictionaries with id, title, due (ISO date),
and archived (boolean). IDs are unique integers. Only unarchived entries appear
in active lists. due_order sorts chronologically. Search is case-insensitive and
matches a literal substring of title. Nothing is persisted. There is no export
or restore operation yet. Access rules live separately in src/access.py.

The factory watches only synthetic/lantern-shelf. The issue repository and target
are this same repository for every report. Read docs/known-issues.md for the entire
synthetic issue history, and factory/gate.yaml for path tiers. These facts are the
same for both evaluation modes; reports and ground-truth labels are NOT repo files.
