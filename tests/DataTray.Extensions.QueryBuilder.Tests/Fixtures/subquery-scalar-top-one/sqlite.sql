SELECT (
  SELECT "Title"
  FROM "Paper"
  ORDER BY "Citations" DESC
  LIMIT 1
) AS "TopPaper";
