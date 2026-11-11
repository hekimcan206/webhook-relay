#!/bin/sh
# Runs once, when the PostgreSQL data directory is first initialised (docker-entrypoint-initdb.d).
# Least privilege:
#   relay_migrator  owns the "relay" and "relay_admin" schemas and runs migrations (DDL);
#   relay_app       is what the relay connects as. It gets DML on "relay" through default privileges, which the
#                   migrations then narrow (the attempt log is append-only: SELECT and INSERT only).
#   "relay_admin"   holds the migration history; the application role cannot see it.
set -eu

: "${MIGRATOR_PASSWORD:?MIGRATOR_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     -v migrator_password="$MIGRATOR_PASSWORD" \
     -v app_password="$APP_PASSWORD" \
     -v db="$POSTGRES_DB" \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE relay_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE relay_app LOGIN PASSWORD :'app_password';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO relay_migrator, relay_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA relay AUTHORIZATION relay_migrator;
CREATE SCHEMA relay_admin AUTHORIZATION relay_migrator;
GRANT USAGE ON SCHEMA relay TO relay_app;

ALTER DEFAULT PRIVILEGES FOR ROLE relay_migrator IN SCHEMA relay
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO relay_app;
ALTER DEFAULT PRIVILEGES FOR ROLE relay_migrator IN SCHEMA relay
    GRANT USAGE, SELECT ON SEQUENCES TO relay_app;
SQL
