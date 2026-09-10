# Prometheus provider

A provider that plugs Prometheus into DataTray. It is **not shipped by default**: it lives under the
repo-root `plugins/` folder (not `src/`) and is staged only in **Debug** builds.

Prometheus is a time-series database with its own query language, so this provider maps its world
onto the host's (SQL-shaped) contract:

- **Schema tree:** connection root → metrics → labels. A metric shows its type (`counter`, `gauge`, …)
  and help text from `/api/v1/metadata`, when the endpoint serves it.
- **Queries:** PromQL, not SQL.
- **Databases:** none — Prometheus has no database layer, so the toolbar picker stays empty.
- **Writes, DDL, routines, users:** not modelled. The HTTP API is read-only and so is this provider.

## Querying

Everything runs through the instant-query endpoint `/api/v1/query`, which is enough for both shapes
of result:

| You write | You get |
|---|---|
| `up` | one row per series, the current sample |
| `sum by (job) (rate(http_requests_total[5m]))` | one row per resulting series |
| `up[1h]` | one row per sample in the last hour |
| `rate(http_requests_total[5m])[6h:1m]` | a subquery — one row per evaluated step |

Results are always flattened to `<labels…>, timestamp, value`, one row per **sample**. That long
format is what a grid can show and what a chart viewer can group by: the label columns are the series
key, `timestamp` is X, `value` is Y. Values arrive as strings from the API so `NaN`, `+Inf` and `-Inf`
survive JSON; they are parsed back to doubles, and anything unparsable becomes null rather than an
error.

There is no paging: an instant query returns the whole result at once, so the page controls are inert.

## Connecting

| Field | Notes |
|---|---|
| URL | e.g. `http://localhost:9090` |
| Username / Password | HTTP basic auth, for endpoints behind a proxy |
| Bearer token | takes precedence over basic auth |

Any Prometheus-compatible query API works — Thanos, Mimir, VictoriaMetrics, Grafana Cloud. Only
`/api/v1/query`, `/api/v1/label/__name__/values` and `/api/v1/labels` are required; the server version
and metric metadata are best-effort and simply stay empty when the endpoint does not serve them.
