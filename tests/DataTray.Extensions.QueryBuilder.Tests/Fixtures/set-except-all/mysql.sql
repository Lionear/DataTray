SELECT `Name`
FROM `Researcher`
EXCEPT ALL
SELECT `Name`
FROM `Institution`;
