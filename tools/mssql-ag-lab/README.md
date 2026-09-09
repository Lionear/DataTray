# `mssql-ag-lab` — a local SQL Server Always On availability group to test against

> **Status: step 1 works.** `compose.yaml` + `bootstrap.sh` bring up a real two-node availability
> group. Step 2 (automatic failover) is still design — see below.

## Running it

```sh
./bootstrap.sh                              # up + form the group (safe to re-run)
docker compose down -v && ./bootstrap.sh    # start over from nothing
docker compose down                         # stop, keep the data
```

About 45 seconds from an empty state, once the image is pulled. It ends by printing the replica and
database states:

```
replica                      role                         health                       connected
---------------------------- ---------------------------- ---------------------------- ----------------
agnode1                      PRIMARY                      HEALTHY                      CONNECTED
agnode2                      SECONDARY                    HEALTHY                      CONNECTED
```

Connect DataTray to `localhost,14331` (primary) or `localhost,14332` (secondary), `sa` /
`Str0ng!Passw0rd` — the same default as `MsSqlProvider.ContainerRecipe`, so the connection dialog's
prefill is already right. Override with `SA_PASSWORD`, `NODE1_PORT`, `NODE2_PORT`, `MSSQL_TAG`.

### Failing over by hand

There is no cluster manager here, so failover is manual — and a plain `ALTER AVAILABILITY GROUP …
FAILOVER` is **rejected**, verified against this lab:

```
Msg 47122 — Cannot failover an availability replica for availability group 'ag1' since it has
CLUSTER_TYPE = NONE. Only force failover is supported in this version of SQL Server.
```

The working sequence, run against this lab. Both replicas are `SYNCHRONOUS_COMMIT` and
`SYNCHRONIZED`, so despite the statement's name nothing is actually lost:

```sql
-- 1. on the target secondary (agnode2, localhost,14332)
ALTER AVAILABILITY GROUP [ag1] FORCE_FAILOVER_ALLOW_DATA_LOSS;

-- 2. on the old primary (agnode1, localhost,14331) — after a forced failover every database
--    there is left suspended (is_suspended = 1, NOT SYNCHRONIZING) and stays that way until:
ALTER DATABASE [AgDemo] SET HADR RESUME;
```

Roles swap, both replicas return to `HEALTHY`, and the database is `SYNCHRONIZED` again. Useful
precisely because it is the shape SE-247's failover tool has to script for `CLUSTER_TYPE = NONE`,
and it is now a thing you can watch happen rather than a paragraph in a mockup.

## Why this exists

DataTray's SQL Server Always On work (SE-247 design, SE-284 dashboard) has no availability group to
run against. There is no live MSSQL fixture anywhere in this repository — every test project runs
offline, and `.github/workflows/test.yml` starts no `services:` container. The consequence showed up
in SE-284: `sys.availability_replicas.backup_priority` was read as `tinyint` because that is what the
documented 0–100 range implied, and the `InvalidCastException` surfaced on a live production server
instead of on a developer machine.

An availability group cannot be simulated with a mock. This lab is the missing fixture.

## Scope: developer harness, not a product feature

This is a harness under `tools/`, alongside the other developer scripts. It is **not** a new
"topology scenario" layer in `plugins/Backends.Docker`, for two reasons:

1. **`DockerComposeBuilder` answers a different question.** It builds one empty container that
   *matches an existing connection* (engine, version, port, credentials), provider-declared through
   `IProviderCatalog`/`ProviderRecipe` — one recipe, one service, one port, one volume, and one
   auto-created host connection per `ManagedContainer`. A multi-node topology with an inter-node
   endpoint network, a certificate exchange and a bootstrap ordering constraint is not a bigger
   recipe; it is a different shape. Nothing here is blocked by that, because nothing here needs it.
2. **SE-247 already drew this line.** Rick decided DataTray does not drive Pacemaker itself: at
   `CLUSTER_TYPE = EXTERNAL` the failover tool shows the exact `pcs`/`crm` command and the user runs
   it, because shell access to a cluster node is a different trust level than a database connection.
   Deploying a cluster from the DataTray UI would cross that same line from the other side.

So the lab is plain files a developer runs by hand. It has no dependency on the app, and the app has
no dependency on it.

## Step 1 — two nodes, a real availability group, manual failover

The base scenario, and the one worth having first: two SQL Server containers on a compose network
with a real AG created between them.

```
  ┌───────────────────────┐        5022/tcp        ┌───────────────────────┐
  │ agnode1               │◄──────────────────────►│ agnode2               │
  │ mssql/server:2025     │   database mirroring   │ mssql/server:2025     │
  │ MSSQL_ENABLE_HADR=1   │   endpoint, cert auth  │ MSSQL_ENABLE_HADR=1   │
  │ :1433 → host 14331    │                        │ :1433 → host 14332    │
  └───────────────────────┘                        └───────────────────────┘
                    CREATE AVAILABILITY GROUP ag1 WITH (CLUSTER_TYPE = NONE)
```

Mechanics, all of it stock:

- **Enabling Always On needs no exec into the container.** `MSSQL_ENABLE_HADR=1` is a documented
  environment variable of the SQL Server Linux image, so `SERVERPROPERTY('IsHadrEnabled') = 1` on
  first boot. (On Windows this is the Configuration Manager checkbox plus a service restart — that
  "the wizard may not offer this" rule from SE-247 stays true, it is just not in the way here.)
- **`CLUSTER_TYPE = NONE`** (SQL Server 2017+). No cluster manager, so no automatic failover — this
  step is deliberately *not* HA. Everything else about the group is genuine: real replicas, real
  synchronisation, real DMV rows.
- **Endpoint authentication is certificate-based.** No AD, no shared Windows identity between two
  containers, so each node creates a certificate, and each imports the other's public certificate to
  authorise the endpoint login. **No shared folder is involved**: `CERTENCODED()` returns the public
  certificate as a binary literal, so `bootstrap.sh` reads it from one node and feeds it straight to
  `CREATE CERTIFICATE … FROM BINARY` on the other. No `BACKUP CERTIFICATE`, no UNC share, no bind
  mount, no uid-10001 permission dance. Worth knowing for SE-247, whose open item assumes the
  file-based path SSMS uses — that path is not the only one, and this one has no filesystem to fail on.
- **Seeding is `AUTOMATIC`**, which needs `GRANT CREATE ANY DATABASE` on the secondary. It saves a
  backup/restore round trip in a throwaway lab.
- **Ordering.** Both containers must be up and the primary's database must have a full backup before
  the group is created. A tiny `sqlcmd`-based bootstrap step after `compose up` is enough; a
  healthcheck plus `depends_on: condition: service_healthy` does not express "the AG is formed", so
  the bootstrap stays an explicit, re-runnable script rather than something hidden in the compose file.

What step 1 buys immediately:

| Question DataTray currently cannot answer locally | Answered by step 1 |
|---|---|
| Do SE-284's five dashboard queries return what the code assumes, against real AG rows? | Yes |
| Actual .NET types of every `sys.dm_hadr_*` column read (the SE-284 class of bug) | Yes — and `sys.dm_exec_describe_first_result_set` can now be checked against rows, not just an empty schema |
| What the tree shows for a secondary replica, an unhealthy replica, a suspended database | Yes |
| How a second instance gets the first one's endpoint certificate with no shared domain (SE-247's open item) | Yes — and the answer turned out to be that no file needs to move at all |
| `sys.availability_group_listeners` with no listener — empty rows, or something the dashboard misreads? | Yes |
| Does automatic failover behave as the dashboard/wizard assume? | **No** — step 2 |

## Step 2 — real automatic failover, on VMs rather than containers

Two facts decide this step, and neither is negotiable:

- **Linux HA needs three nodes, not two.** With `CLUSTER_TYPE = EXTERNAL` there is no WSFC and no
  file-share witness, so the configuration metadata lives in the SQL Server instances themselves and
  a third instance is required to arbitrate. Since SQL Server 2017 CU1 the supported minimum is
  *two synchronous replicas plus a configuration-only replica*, and the configuration-only replica
  may run SQL Server Express. So step 2 is `agnode1` + `agnode2` + a small `agnode3`, not a
  two-node cluster — two nodes with Pacemaker is a split-brain generator, not an HA lab.
- **Pacemaker does not belong in the SQL Server container image.** The `mcr.microsoft.com/mssql/server`
  image carries no systemd and no `mssql-server-ha` resource agents, and Microsoft documents this
  configuration only on hosts. Doing it in Docker means a hand-rolled image (Ubuntu + `mssql-server`
  + `mssql-server-ha` + `pacemaker`/`corosync` + a supervisor to replace systemd) in privileged
  containers with fencing disabled — a large amount of bespoke, unsupported machinery whose failure
  modes would be the *lab's* failure modes, not SQL Server's. For a fixture whose entire purpose is
  to reproduce real behaviour faithfully, that is the wrong trade.

Step 2 therefore runs on three small VMs (multipass / Lima / Vagrant — pick one when the step is
picked up) following the documented RHEL/Ubuntu Pacemaker path. The lab holds two runtimes, which is
the honest cost of the difference between "an availability group" and "an availability group that
fails over on its own".

Two consequences worth knowing before the dashboard is trusted against step 2:

- **There is no listener in the Windows sense.** Pacemaker has no virtual network name; the listener
  is an `ocf:heartbeat:IPaddr2` virtual IP, and the *name* only exists if it is registered in DNS by
  hand. A "listener" in this lab is an IP plus a hosts-file entry.
- **Cluster DMVs go quiet.** Microsoft states that availability group DMVs querying cluster
  information return empty rows on Pacemaker clusters. Any dashboard field sourced from one of those
  is blank on `EXTERNAL` and must not read as "broken".

## What this changes in DataTray — one real gap so far

Nothing in `plugins/Backends.Docker` and nothing in `src/DataTray.Providers.MsSql` changes to build
the lab. One gap is already visible from reading the provider, though, and step 2 will hit it:

`MsSqlProvider.ConnectionFields` has no `ApplicationIntent` and no `MultiSubnetFailover`, and
`BuildConnectionString` sets neither. `ParseConnectionString` drops both on paste, since it only maps
keys it knows. So a read-only-routing connection string (`ApplicationIntent=ReadOnly`) survives
neither a paste nor a round trip through the connection editor today. That is a small, additive
change to the two field lists — its own ticket, not part of the lab, and only actually needed once
there is a listener to point it at.

## Plan

1. ✅ **`compose.yaml` + `bootstrap.sh`** — two nodes, certificate exchange, `CREATE AVAILABILITY
   GROUP` with `CLUSTER_TYPE = NONE`, one auto-seeded database with rows. Both replicas come up
   `HEALTHY`/`CONNECTED` with `AgDemo` `SYNCHRONIZED` on both sides, and a forced failover and
   resume were run against it end to end.
2. **Verify SE-284's queries against it** — run the dashboard's five queries and the tree probe and
   compare every column's real .NET type against what the code reads. **Not done here**: SE-284
   landed on `develop` (PRs #191/#192) and this branch predates it, so there is no
   `AvailabilityGroupDashboardView` in this worktree to check. Needs a branch off `develop`.
   The one spot-check that *was* possible confirms the premise — against real replica rows,
   `sys.availability_replicas.backup_priority` is `int`, while `failover_mode` and
   `availability_mode` really are `tinyint` — so PR #192's blanket `Convert.ToInt32` was the right call.
3. ✅ **SE-247's certificate open item** — answered, and not the way the ticket assumed: with
   `CERTENCODED()` + `FROM BINARY` no certificate file has to move between instances at all, so the
   shared-UNC-path problem that motivated the open item can be sidestepped entirely. Worth a comment
   on SE-247.
4. **Step 2 (separate ticket)** — three VMs, Pacemaker, `CLUSTER_TYPE = EXTERNAL`, virtual IP. Only
   then is the SE-247 failover tool testable, and only then is `EXTERNAL`'s "the tool must refuse and
   say why" path reachable.

Step 2 is its own PR, and nothing in step 1 constrains it: the bootstrap SQL is the same statements
with a different `CLUSTER_TYPE`, and the lab folder gets a second subfolder rather than a rewrite.

## Gotchas this cost, so the next person doesn't pay them again

- **`sqlcmd` truncates a large value to a 256-character display width, silently.** The endpoint
  certificate hex is ~1900 characters, so the importing node just reports `Msg 15468 — An error
  occurred during the generation of the certificate`, which says nothing about truncation. `-W` and
  `-y` are mutually exclusive, and so are `-h -1` and `-y 0`, so the combination that works is
  `-h -1 -y 8000`.
- **`-y` right-aligns numbers.** `SELECT COUNT(*)` comes back as `"          0"`, so a trailing-only
  trim leaves a string that is not `"0"` — which inverted every "does this already exist?" guard in
  the script and made a fresh lab claim it was already built. Trim both ends.

## Open

- Which VM runtime for step 2 (multipass, Lima, Vagrant) — deferred to when step 2 starts.
- Whether the lab should ever run in CI. Step 1 could (two containers, a few minutes); step 2 cannot.
  Not decided, and not a reason to shape step 1 differently.
- Image tag: the lab pins `2025-latest` to match `MsSqlProvider.ContainerRecipe`'s default. If AG
  behaviour needs checking against 2019/2022 as well, the tag becomes a variable — one line, when
  something actually needs it.
