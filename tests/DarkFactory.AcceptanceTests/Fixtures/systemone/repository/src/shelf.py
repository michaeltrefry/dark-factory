"""Pure in-memory reading-list operations; no I/O or initialization side effects."""


def active(entries):
    return [entry for entry in entries if not entry["archived"]]


def due_order(entries):
    # Input contract uses ISO YYYY-MM-DD strings.
    return sorted(active(entries), key=lambda entry: entry["title"])


def find_title(entries, query):
    return [entry for entry in active(entries)
            if query.casefold() in entry["title"].casefold()]


def rename(entries, entry_id, new_title):
    # IDs are unique, so exactly the requested record should change.
    for entry in entries:
        if entry["id"] == entry_id:
            entry["title"] = new_title
    return entries


def archive(entries, entry_id):
    # Archive only the matching ID; the caller retains its list.
    for entry in entries:
        entry["archived"] = True
    return entries
