#!/bin/bash
set -e
# Local-dev only bootstrap. Creates one logical DB + least-privilege role per stateful component.
# No service role is granted access to another service database (verified by SEC-08 later).
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<EOSQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'identity_app') THEN
    CREATE ROLE identity_app WITH LOGIN PASSWORD '${IDENTITY_DB_PASSWORD:-identity_dev_pw}';
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'driver_app') THEN
    CREATE ROLE driver_app WITH LOGIN PASSWORD '${DRIVER_DB_PASSWORD:-driver_dev_pw}';
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'trip_app') THEN
    CREATE ROLE trip_app WITH LOGIN PASSWORD '${TRIP_DB_PASSWORD:-trip_dev_pw}';
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'matching_app') THEN
    CREATE ROLE matching_app WITH LOGIN PASSWORD '${MATCHING_DB_PASSWORD:-matching_dev_pw}';
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payment_app') THEN
    CREATE ROLE payment_app WITH LOGIN PASSWORD '${PAYMENT_DB_PASSWORD:-payment_dev_pw}';
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'notification_app') THEN
    CREATE ROLE notification_app WITH LOGIN PASSWORD '${NOTIFICATION_DB_PASSWORD:-notification_dev_pw}';
  END IF;
END
\$\$;
SELECT 'roles ensured' AS status;
EOSQL

for db in identity_db driver_db trip_db matching_db payment_db notification_db; do
  role="${db%_db}_app"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -tc "SELECT 1 FROM pg_database WHERE datname = '$db'" | grep -q 1 || psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -c "CREATE DATABASE $db OWNER $role"
done
for db in identity_db driver_db trip_db matching_db payment_db notification_db; do
  role="${db%_db}_app"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" -c "GRANT ALL ON SCHEMA public TO $role"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" -c "ALTER DEFAULT PRIVILEGES FOR ROLE $role IN SCHEMA public GRANT ALL ON TABLES TO $role"
done
echo "databases ensured with isolated owners"

# Enforce DB-level isolation: revoke default PUBLIC connect, grant only to owner.
for db in identity_db driver_db trip_db matching_db payment_db notification_db; do
  role="${db%_db}_app"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -c "REVOKE CONNECT ON DATABASE $db FROM PUBLIC"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -c "GRANT CONNECT ON DATABASE $db TO $role"
done
echo "database connect isolation enforced"
