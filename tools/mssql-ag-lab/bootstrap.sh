#!/usr/bin/env bash
#
# Bring up the two-node lab and form a real availability group between the nodes.
#
#   ./bootstrap.sh                      # up + form the AG (safe to re-run)
#   docker compose down -v && ./bootstrap.sh   # start over from nothing
#
# Re-runnable: every step is guarded, so running it against an already-formed lab is a no-op that
# just reprints the status. See README.md for what this does and does not prove.

set -euo pipefail
cd "$(dirname "$0")"

# Matches MsSqlProvider.ContainerRecipe's default, so a connection DataTray prefills just works.
export SA_PASSWORD="${SA_PASSWORD:-Str0ng!Passw0rd}"
AG_NAME="${AG_NAME:-ag1}"
AG_DB="${AG_DB:-AgDemo}"
NODE1_PORT="${NODE1_PORT:-14331}"
NODE2_PORT="${NODE2_PORT:-14332}"
export NODE1_PORT NODE2_PORT

# sqlcmd is in the image but not on PATH, and tools18 defaults to encrypt=mandatory (hence -C).
# -b makes a T-SQL error a non-zero exit, so set -e actually catches one.
SQLCMD=(/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b)

say() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

# sql <service> <query>  — run a batch, show its output. -Y 28 keeps the report readable: these
# columns are sysname/nvarchar(128) and would otherwise each pad out to 128 characters.
sql() { docker compose exec -T "$1" "${SQLCMD[@]}" -P "$SA_PASSWORD" -Y 28 -Q "$2"; }

# val <service> <query>  — run a scalar query, return the bare value (no header, no padding).
# -y 8000 rather than -W: sqlcmd's default display width for a large varchar is 256 characters, and
# it truncates silently — which cuts the ~1900-character certificate hex in half and surfaces only
# as "Msg 15468 ... error during the generation of the certificate" on the importing node. -W and
# -y are mutually exclusive, and so are -h -1 and -y 0, so it is an explicit width plus our own trim.
# Trim both ends: a varchar comes back left-aligned but a number comes back right-aligned, so a
# trailing-only trim turns COUNT(*) into "          0", which is not "0" and quietly inverts every
# "does this already exist?" guard below.
val() {
    docker compose exec -T "$1" "${SQLCMD[@]}" -P "$SA_PASSWORD" -h -1 -y 8000 -Q "$2" \
        | head -1 | tr -d '\r' | sed 's/^[[:space:]]*//; s/[[:space:]]*$//'
}

wait_healthy() {
    local svc=$1
    printf 'Waiting for %s' "$svc"
    for _ in $(seq 1 60); do
        if [ "$(docker inspect -f '{{.State.Health.Status}}' "$svc" 2>/dev/null)" = healthy ]; then
            printf ' — up\n'
            return 0
        fi
        printf '.'
        sleep 5
    done
    printf '\n'
    echo "ERROR: $svc never became healthy. Try: docker compose logs $svc" >&2
    return 1
}

# ---------------------------------------------------------------------------------------------
say "Starting both nodes"
docker compose up -d
wait_healthy agnode1
wait_healthy agnode2

for node in agnode1 agnode2; do
    hadr=$(val "$node" "SELECT SERVERPROPERTY('IsHadrEnabled')")
    name=$(val "$node" "SELECT SERVERPROPERTY('ServerName')")
    [ "$hadr" = "1" ] || { echo "ERROR: Always On is off on $node (IsHadrEnabled=$hadr)." >&2; exit 1; }
    # The AG replica names below are these values; if the hostname didn't take, fail here rather
    # than inside CREATE AVAILABILITY GROUP with a less obvious error.
    [ "$name" = "$node" ] || { echo "ERROR: $node reports ServerName '$name', expected '$node'." >&2; exit 1; }
done
echo "Always On is enabled on both nodes."

# ---------------------------------------------------------------------------------------------
# Endpoint identity. Two containers share no domain, so the mirroring endpoints authenticate each
# other with certificates: each node makes one, and imports the other's public half.
say "Creating master key, certificate and mirroring endpoint on each node"
for node in agnode1 agnode2; do
    sql "$node" "
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
    CREATE MASTER KEY ENCRYPTION BY PASSWORD = '$SA_PASSWORD';

IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = '${node}_cert')
    CREATE CERTIFICATE [${node}_cert] WITH SUBJECT = '${node} AG endpoint';

IF NOT EXISTS (SELECT 1 FROM sys.database_mirroring_endpoints WHERE name = 'hadr_endpoint')
    CREATE ENDPOINT hadr_endpoint
        AS TCP (LISTENER_PORT = 5022)
        FOR DATABASE_MIRRORING (
            ROLE = ALL,
            AUTHENTICATION = CERTIFICATE [${node}_cert],
            ENCRYPTION = REQUIRED ALGORITHM AES);

ALTER ENDPOINT hadr_endpoint STATE = STARTED;"
done

# CERTENCODED() hands back the public certificate as a binary literal, so the exchange needs no
# shared filesystem at all — no BACKUP CERTIFICATE, no UNC share, no bind mount and no uid dance.
# (Worth knowing for SE-247: the file-based path SSMS assumes is not the only one.)
say "Exchanging endpoint certificates"
exchange() {
    local from=$1 to=$2
    local hex
    hex=$(val "$from" "SELECT CONVERT(varchar(max), CERTENCODED(CERT_ID('${from}_cert')), 1)")
    case "$hex" in
        0x*) ;;
        *) echo "ERROR: unexpected certificate payload from $from: $hex" >&2; return 1 ;;
    esac

    sql "$to" "
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '${from}_login')
    CREATE LOGIN [${from}_login] WITH PASSWORD = '$SA_PASSWORD';

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '${from}_user')
    CREATE USER [${from}_user] FOR LOGIN [${from}_login];

IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = '${from}_cert')
    CREATE CERTIFICATE [${from}_cert] AUTHORIZATION [${from}_user] FROM BINARY = ${hex};

GRANT CONNECT ON ENDPOINT::hadr_endpoint TO [${from}_login];"
}
exchange agnode1 agnode2
exchange agnode2 agnode1

# ---------------------------------------------------------------------------------------------
# CLUSTER_TYPE = NONE: a genuine availability group with no cluster manager, so FAILOVER_MODE can
# only be MANUAL. That is the deliberate ceiling of step 1 — see README.md.
say "Creating availability group $AG_NAME"
if [ "$(val agnode1 "SELECT COUNT(*) FROM sys.availability_groups WHERE name = '$AG_NAME'")" = "0" ]; then
    sql agnode1 "
CREATE AVAILABILITY GROUP [$AG_NAME]
    WITH (CLUSTER_TYPE = NONE)
    FOR REPLICA ON
        N'agnode1' WITH (
            ENDPOINT_URL = N'tcp://agnode1:5022',
            AVAILABILITY_MODE = SYNCHRONOUS_COMMIT,
            FAILOVER_MODE = MANUAL,
            SEEDING_MODE = AUTOMATIC),
        N'agnode2' WITH (
            ENDPOINT_URL = N'tcp://agnode2:5022',
            AVAILABILITY_MODE = SYNCHRONOUS_COMMIT,
            FAILOVER_MODE = MANUAL,
            SEEDING_MODE = AUTOMATIC);"
else
    echo "Availability group $AG_NAME already exists."
fi

if [ "$(val agnode2 "SELECT COUNT(*) FROM sys.availability_groups WHERE name = '$AG_NAME'")" = "0" ]; then
    # GRANT CREATE ANY DATABASE is what lets automatic seeding create the database on this side.
    sql agnode2 "
ALTER AVAILABILITY GROUP [$AG_NAME] JOIN WITH (CLUSTER_TYPE = NONE);
ALTER AVAILABILITY GROUP [$AG_NAME] GRANT CREATE ANY DATABASE;"
else
    echo "agnode2 has already joined $AG_NAME."
fi

# ---------------------------------------------------------------------------------------------
# A database only joins if it is FULL recovery and has had one full backup.
say "Creating database $AG_DB and adding it to the group"
if [ "$(val agnode1 "SELECT COUNT(*) FROM sys.databases WHERE name = '$AG_DB'")" = "0" ]; then
    sql agnode1 "
CREATE DATABASE [$AG_DB];
ALTER DATABASE [$AG_DB] SET RECOVERY FULL;"

    sql agnode1 "
USE [$AG_DB];
CREATE TABLE dbo.Orders (
    Id       int IDENTITY(1,1) PRIMARY KEY,
    Customer nvarchar(100) NOT NULL,
    Total    decimal(10,2) NOT NULL,
    PlacedAt datetime2     NOT NULL DEFAULT SYSUTCDATETIME());
INSERT INTO dbo.Orders (Customer, Total)
VALUES (N'Acme', 129.50), (N'Umbrella', 8420.00), (N'Initech', 12.75);"

    sql agnode1 "BACKUP DATABASE [$AG_DB] TO DISK = '/var/opt/mssql/data/$AG_DB.bak' WITH INIT, FORMAT;"
else
    echo "Database $AG_DB already exists."
fi

if [ "$(val agnode1 "SELECT COUNT(*) FROM sys.availability_databases_cluster WHERE database_name = '$AG_DB'")" = "0" ]; then
    sql agnode1 "ALTER AVAILABILITY GROUP [$AG_NAME] ADD DATABASE [$AG_DB];"
else
    echo "$AG_DB is already in $AG_NAME."
fi

# ---------------------------------------------------------------------------------------------
say "Waiting for the secondary to finish seeding"
for _ in $(seq 1 60); do
    state=$(val agnode2 "
SELECT TOP 1 synchronization_state_desc
FROM sys.dm_hadr_database_replica_states s
JOIN sys.availability_databases_cluster c ON c.group_database_id = s.group_database_id
WHERE c.database_name = '$AG_DB' AND s.is_local = 1" || true)
    [ "$state" = "SYNCHRONIZED" ] && break
    printf '.'
    sleep 5
done
printf '\n'

# ---------------------------------------------------------------------------------------------
say "Replica states"
sql agnode1 "
SELECT ar.replica_server_name          AS replica,
       ars.role_desc                    AS role,
       ars.synchronization_health_desc  AS health,
       ars.connected_state_desc         AS connected
FROM sys.dm_hadr_availability_replica_states ars
JOIN sys.availability_replicas ar ON ar.replica_id = ars.replica_id
WHERE ars.group_id = (SELECT group_id FROM sys.availability_groups WHERE name = '$AG_NAME')
ORDER BY ar.replica_server_name;"

say "Database states"
sql agnode1 "
SELECT c.database_name                 AS [database],
       ar.replica_server_name          AS replica,
       drs.synchronization_state_desc  AS state,
       drs.is_suspended                AS suspended
FROM sys.dm_hadr_database_replica_states drs
JOIN sys.availability_replicas ar ON ar.replica_id = drs.replica_id
JOIN sys.dm_hadr_database_replica_cluster_states c
     ON c.replica_id = drs.replica_id AND c.group_database_id = drs.group_database_id
WHERE drs.group_id = (SELECT group_id FROM sys.availability_groups WHERE name = '$AG_NAME')
ORDER BY c.database_name, ar.replica_server_name;"

cat <<EOF

$AG_NAME is up. Connect DataTray to either node:

  agnode1 (primary)    localhost,$NODE1_PORT   sa / $SA_PASSWORD
  agnode2 (secondary)  localhost,$NODE2_PORT   sa / $SA_PASSWORD

Manual failover (this is a CLUSTER_TYPE = NONE group, so it is never automatic) — see README.md.
EOF
