SELECT r.`Name`,
       p.`Title`,
       c.`Name`
FROM `Researcher` AS r
INNER JOIN `PaperAuthor` AS pa ON r.`Id` = pa.`ResearcherId`
LEFT JOIN `Paper` AS p ON pa.`PaperId` = p.`Id`
LEFT JOIN `Conference` AS c ON p.`ConferenceId` = c.`Id`;
