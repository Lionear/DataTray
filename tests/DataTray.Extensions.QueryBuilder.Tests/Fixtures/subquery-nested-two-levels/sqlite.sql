SELECT "Id"
FROM "Researcher"
WHERE "InstitutionId" IN (
  SELECT "Id"
  FROM "Institution"
  WHERE "Id" IN (
    SELECT "InstitutionId"
    FROM "Department"
  )
);
