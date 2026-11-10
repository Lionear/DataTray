SELECT r.`Name`,
       t.`N`
FROM `Researcher` AS r
INNER JOIN (
  SELECT `ResearcherId`,
         COUNT(*) AS `N`
  FROM `Grant`
  GROUP BY `ResearcherId`
) AS t ON r.`Id` = t.`ResearcherId`;
