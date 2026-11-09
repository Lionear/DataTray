WITH "Top" AS (
  SELECT "Name"
  FROM "Researcher"
  ORDER BY "HIndex" DESC
  LIMIT 5
)
SELECT "Name"
FROM "Top";
