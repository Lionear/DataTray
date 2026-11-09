SELECT t."Name"
FROM (
  SELECT "Name"
  FROM "Researcher"
  ORDER BY "Id"
  LIMIT 2
) AS t;
