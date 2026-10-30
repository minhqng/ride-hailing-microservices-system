#!/bin/sh
set -eu

seed="${1:?seed node name required, for example rabbit@rabbitmq}"

rabbitmq-server &
server_pid=$!
trap 'kill "$server_pid" 2>/dev/null || true' INT TERM EXIT

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
