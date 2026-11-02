#!/bin/sh
set -eu
: "${RABBITMQ_ERLANG_COOKIE:?HA cookie must be initialized}"
# Initialize before either the server or a CLI can create a random cookie.
# Refuse rotation on an existing cluster volume.
cookie=/var/lib/rabbitmq/.erlang.cookie
if [ -s "$cookie" ] && [ "$(cat "$cookie")" != "$RABBITMQ_ERLANG_COOKIE" ]; then
    echo "HA cookie differs from the existing cluster volume; refusing implicit rotation" >&2
    exit 1
fi
umask 077
printf '%s' "$RABBITMQ_ERLANG_COOKIE" > "$cookie"
chown rabbitmq:rabbitmq "$cookie"
chmod 600 "$cookie"
if [ "$#" -gt 0 ]; then
    exec gosu rabbitmq sh /etc/rabbitmq/join-cluster.sh "$1"
fi
exec /usr/local/bin/docker-entrypoint.sh rabbitmq-server
