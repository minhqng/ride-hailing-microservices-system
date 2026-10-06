#!/bin/sh
set -eu
rabbitmq-diagnostics -q ping
# Import after normal bootstrap so configured default credentials remain intact.
# Policies update existing queues without redeclaration or deleting messages.
policy=/etc/rabbitmq/ride-reliability.json
stamp=/tmp/ride-reliability.sha256
expected=$(sha256sum "$policy")
if [ ! -f "$stamp" ] || [ "$(cat "$stamp")" != "$expected" ]; then
    rabbitmqctl import_definitions "$policy"
    printf '%s\n' "$expected" > "$stamp"
fi
