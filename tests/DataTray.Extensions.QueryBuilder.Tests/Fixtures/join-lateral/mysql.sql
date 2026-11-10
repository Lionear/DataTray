SELECT i.`Name`,
       x.`Name`
FROM `Institution` AS i
INNER JOIN LATERAL (
  SELECT `Name`
  FROM `Researcher`
  WHERE `InstitutionId` = i.`Id`
) AS x ON TRUE;
