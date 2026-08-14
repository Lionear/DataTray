- **The Activity Monitor can be narrowed to one database, or to just the blocking sessions.** Two new
  toolbar controls: a **Database** dropdown listing the databases that actually have sessions right now,
  and a **Blocking only** checkbox that keeps the blocked sessions *and* the sessions blocking them — so
  the culprit is on screen next to the victim, not filtered away. Both filter the snapshot you already
  have, so they apply instantly and survive auto-refresh. On SQL Server both are available; Postgres and
  MySQL get the Database filter (their session views have no blocker column).
