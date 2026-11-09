WITH "Outer" AS (
  WITH "Inner" AS (
    SELECT "Name"
    FROM "Researcher"
  )
  SELECT "Name"
  FROM "Inner"
)
SELECT "Name"
FROM "Outer";
