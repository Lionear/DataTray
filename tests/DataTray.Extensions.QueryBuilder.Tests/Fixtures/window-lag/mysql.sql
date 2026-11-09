SELECT `Name`,
       LAG(`HIndex`, 1) OVER (PARTITION BY `InstitutionId` ORDER BY `Id`) AS `W`
FROM `Researcher`;
