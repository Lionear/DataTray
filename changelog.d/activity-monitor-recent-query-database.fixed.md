The Activity Monitor's Recent Expensive Queries grid showed an empty Database for almost every row.
`sys.dm_exec_sql_text` only fills in a database for compiled objects and leaves it NULL for ad-hoc
batches, which is most of that grid; the database a query actually ran against is on the plan, and is now
read from there. The same statement executed against several databases is therefore several rows again —
one per database, each with its own cost — instead of one row labelled with an arbitrary one of them.
