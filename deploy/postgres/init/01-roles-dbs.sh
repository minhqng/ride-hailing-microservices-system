#!/bin/bash
set -euo pipefail
# Bootstrap only: runs on a fresh project volume. SQL quoting protects configured passwords.
for service in identity driver trip matching payment notification; do
  export RIDE_BOOTSTRAP_ROLE="${service}_app"
  export RIDE_BOOTSTRAP_DB="${service}_db"
  password_var="${service^^}_DB_PASSWORD"
  export RIDE_BOOTSTRAP_PASSWORD="${!password_var}"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv role RIDE_BOOTSTRAP_ROLE
\getenv db RIDE_BOOTSTRAP_DB
\getenv password RIDE_BOOTSTRAP_PASSWORD
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'role', :'password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'role') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', :'db', :'role')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db') \gexec
SELECT format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', :'db') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'db', :'role') \gexec
SQL
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$RIDE_BOOTSTRAP_DB" <<'SQL'
\getenv role RIDE_BOOTSTRAP_ROLE
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT USAGE, CREATE ON SCHEMA public TO %I', :'role') \gexec
SQL
done
unset RIDE_BOOTSTRAP_PASSWORD
