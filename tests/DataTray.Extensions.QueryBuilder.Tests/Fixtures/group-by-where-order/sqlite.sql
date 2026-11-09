SELECT "Year",
       SUM("Citations") AS "C"
FROM "Paper"
WHERE "Year" > 2000
GROUP BY "Year"
ORDER BY SUM("Citations") DESC;
