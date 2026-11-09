SELECT `Name`,
       FIRST_VALUE(`Name`) OVER (PARTITION BY `InstitutionId` ORDER BY `HIndex` DESC) AS `W`
FROM `Researcher`;
