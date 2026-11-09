SELECT "Id"
FROM "Researcher"
WHERE "HIndex" > ANY (
  SELECT "HIndex"
  FROM "Researcher"
  WHERE "InstitutionId" = 1
);
