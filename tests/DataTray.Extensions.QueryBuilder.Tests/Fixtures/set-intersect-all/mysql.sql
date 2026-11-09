SELECT `Name`
FROM `Researcher`
INTERSECT ALL
SELECT `Name`
FROM `Institution`;
