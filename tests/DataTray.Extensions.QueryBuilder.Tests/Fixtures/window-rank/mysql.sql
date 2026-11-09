SELECT `Name`,
       RANK() OVER (PARTITION BY `InstitutionId` ORDER BY `HIndex` DESC) AS `W`
FROM `Researcher`;
