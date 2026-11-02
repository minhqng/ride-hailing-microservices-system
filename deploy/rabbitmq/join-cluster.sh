#!/bin/sh
set -eu

seed="${1:?seed node name required, for example rabbit@rabbitmq}"

marker=/var/lib/rabbitmq/.ride-cluster-seed
if [ -f "$marker" ]; then
    [ "$(cat "$marker")" = "$seed" ] || { echo "Configured seed differs from persisted cluster" >&2; exit 1; }
    rejoin=false
else
    mnesia="${RABBITMQ_MNESIA_BASE:-/var/lib/rabbitmq/mnesia}"
    if [ -d "$mnesia" ] && [ -n "$(ls -A "$mnesia")" ]; then
        echo "Existing broker data has no initialization marker; refusing destructive join" >&2
        exit 1
    fi
    rejoin=true
fi

rabbitmq-server &
server_pid=$!
trap 'kill "$server_pid" 2>/dev/null || true' INT TERM EXIT

# The CLI can run before the Erlang distribution port exists. Keep that
# readiness race separate from join failures, and stop if the server exits.
attempt=0
until rabbitmq-diagnostics -q ping >/dev/null 2>&1; do
    kill -0 "$server_pid" 2>/dev/null || exit 1
    attempt=$((attempt + 1))
    [ "$attempt" -lt 60 ] || exit 1
    sleep 1
done
rabbitmqctl await_startup
if [ "$rejoin" = true ]; then
    attempt=0
    until rabbitmq-diagnostics -q -n "$seed" ping; do
        attempt=$((attempt + 1))
        [ "$attempt" -lt 60 ] || exit 1
        sleep 1
    done
    # join_cluster resets local state in preparation for first membership.
    # Never run it again against persisted quorum data on a normal restart.
    rabbitmqctl join_cluster "$seed"
    umask 077
    printf '%s' "$seed" > "${marker}.tmp"
    mv "${marker}.tmp" "$marker"
else
    echo "Restoring persisted membership without join_cluster"
fi
trap - INT TERM EXIT
wait "$server_pid"
