"""Access decisions, separate from list manipulation. No I/O."""


def can_edit(role):
    """Only editors and owners may edit; viewers must be read-only."""
    return role in ("viewer", "editor", "owner")
