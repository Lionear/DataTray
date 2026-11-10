SELECT "InstitutionId"
FROM "Researcher"
GROUP BY "InstitutionId"
HAVING COUNT(*) > 5 OR MAX("HIndex") > 50 AND MIN("HIndex") < 5;
