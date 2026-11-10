SELECT r."Name"
FROM "Researcher" AS r
INNER JOIN "Institution" AS i ON r."InstitutionId" = i."Id" AND r."HIndex" > 10;
