#!/bin/bash
# Runs once, the first time the Postgres container starts with an empty data volume.
# Each service gets its own database and its own user that can only use that database.
set -e

# By default every user may connect to the built-in databases. Remove that,
# so a service user can only reach its own database.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<'EOSQL'
REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;
REVOKE CONNECT ON DATABASE template1 FROM PUBLIC;
EOSQL

create_db() {
  local db=$1
  local user=$2
  local password=$3

  echo "Creating database '$db' owned by user '$user'"

  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
    -v db="$db" -v user="$user" -v password="$password" <<'EOSQL'
CREATE USER :"user" WITH PASSWORD :'password';
CREATE DATABASE :"db" OWNER :"user";
-- Same as above: only the owner (and the superuser) may connect.
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
EOSQL
}

create_db book_db   book_user   "$BOOK_DB_PASSWORD"
create_db member_db member_user "$MEMBER_DB_PASSWORD"
create_db loan_db   loan_user   "$LOAN_DB_PASSWORD"
