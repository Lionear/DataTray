WITH "Sorted" AS (
  SELECT "Name"
  FROM "Researcher"
  ORDER BY "Name"
)
SELECT "Name"
FROM "Sorted";
