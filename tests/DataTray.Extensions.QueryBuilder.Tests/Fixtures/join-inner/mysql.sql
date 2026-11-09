SELECT r.`Name`,
       i.`Name`
FROM `Researcher` AS r
INNER JOIN `Institution` AS i ON r.`InstitutionId` = i.`Id`;
