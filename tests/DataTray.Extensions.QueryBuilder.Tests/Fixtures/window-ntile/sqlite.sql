SELECT "Name",
       NTILE(4) OVER (ORDER BY "HIndex") AS "W"
FROM "Researcher";
