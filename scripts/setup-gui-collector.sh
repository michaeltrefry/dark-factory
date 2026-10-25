#!/bin/bash
# No implicit sudo, downloads, account changes or registration in preflight/dry-run.
set -euo pipefail
here="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
exec /usr/bin/python3 -I "$here/setup-gui-collector.py" "$@"
