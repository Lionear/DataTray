SELECT r.`Name`,
       i.`Name`
FROM `Researcher` AS r
LEFT JOIN `Institution` AS i ON r.`InstitutionId` = i.`Id`;
