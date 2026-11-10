SELECT "Name",
       COUNT(*) OVER (PARTITION BY "InstitutionId") AS "W"
FROM "Researcher";
