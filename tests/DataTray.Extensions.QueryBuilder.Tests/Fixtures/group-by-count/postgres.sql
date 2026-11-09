SELECT "InstitutionId",
       COUNT(*) AS "N"
FROM "Researcher"
GROUP BY "InstitutionId";
