WITH RECURSIVE "Seq" ("n") AS (
  SELECT 1
  UNION ALL
  SELECT "n" + 1
  FROM "Seq"
  WHERE "n" < 10
)
SELECT "n"
FROM "Seq";
