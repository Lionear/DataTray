SELECT `Name`
FROM `Researcher`
UNION
SELECT `Name`
FROM `Institution`
ORDER BY `Name`
LIMIT 10;
