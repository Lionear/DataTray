WITH "Names" ("n", "h") AS (
  SELECT "Name",
         "HIndex"
  FROM "Researcher"
)
SELECT "n"
FROM "Names";
