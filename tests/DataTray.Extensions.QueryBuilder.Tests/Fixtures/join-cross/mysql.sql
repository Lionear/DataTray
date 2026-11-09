SELECT r.`Name`,
       c.`Name`
FROM `Researcher` AS r
CROSS JOIN `Conference` AS c;
