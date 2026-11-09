SELECT t.`Name`
FROM (
  SELECT `Name`,
         ROW_NUMBER() OVER (PARTITION BY `InstitutionId` ORDER BY `HIndex` DESC) AS `rn`
  FROM `Researcher`
) AS t
WHERE t.`rn` = 1;
