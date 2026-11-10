SELECT r."Name",
       i."Name"
FROM "Researcher" AS r
FULL JOIN "Institution" AS i ON r."InstitutionId" = i."Id";
