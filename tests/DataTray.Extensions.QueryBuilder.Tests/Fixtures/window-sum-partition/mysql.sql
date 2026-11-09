SELECT `Name`,
       SUM(`Salary`) OVER (PARTITION BY `InstitutionId`) AS `W`
FROM `Researcher`;
