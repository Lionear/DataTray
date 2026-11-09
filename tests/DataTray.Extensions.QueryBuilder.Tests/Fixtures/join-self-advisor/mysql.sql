SELECT s.`Name` AS `Student`,
       a.`Name` AS `Advisor`
FROM `Researcher` AS s
LEFT JOIN `Researcher` AS a ON s.`AdvisorId` = a.`Id`;
