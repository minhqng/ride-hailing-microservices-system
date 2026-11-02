#!/bin/sh
set -eu

seed="${1:?seed node name required, for example rabbit@rabbitmq}"

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
attempt=0
until rabbitmq-diagnostics -q -n "$seed" ping; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 60 ]; then
        echo "Seed $seed did not become reachable" >&2
        exit 1
    fi
    sleep 1
done

# RabbitMQ 4.1+ join_cluster performs the required preparation while the node
# is running. This local-test script must never be used to merge nodes that
# contain independent data.
rabbitmqctl join_cluster "$seed"
trap - INT TERM EXIT
wait "$server_pid"
