#!/usr/bin/env bash
#
# Run the Node tests inside their own transient systemd scope with a memory ceiling.
#
# Why: a failing assertion in this suite pretty-prints whatever it was handed. When that value is
# one of the DOM shim's elements, the print follows the shim's back-links out into the whole
# document and re-expands it at every level, so a single failing test has consumed tens of GB and
# been OOM-killed. Because tmux 3.6 puts each pane in its own scope with OOMPolicy=stop, that kill
# also stops the scope, which killed every process in the window and made the window vanish
# mid-run. The harness now keeps those links non-enumerable (see link() in tests/support/harness.js),
# but this wrapper means a future runaway can only take down its own scope.
#
# Usage:
#   scripts/test-safe.sh                       # the whole unit suite, 4G ceiling
#   scripts/test-safe.sh tests/profile-layout.test.js
#   TEST_MEMORY_MAX=8G scripts/test-safe.sh
#
# Falls back to a plain `node --test` when systemd-run is unavailable (CI, containers, other hosts).
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

MEM="${TEST_MEMORY_MAX:-4G}"

if [ "${1:-}" = "--in-scope" ]; then
    shift
elif command -v systemd-run >/dev/null 2>&1 \
    && systemd-run --user --scope --quiet -p "MemoryMax=$MEM" -p OOMPolicy=continue \
        "$(command -v true)" >/dev/null 2>&1; then
    exec systemd-run --user --scope --quiet \
        -p "MemoryMax=$MEM" -p OOMPolicy=continue \
        "$(command -v bash)" "$0" --in-scope "$@"
else
    echo "test-safe: systemd-run unavailable, running without a memory ceiling" >&2
fi

if [ "$#" -eq 0 ]; then
    set -- tests/*.test.js
fi
exec node --test "$@"
