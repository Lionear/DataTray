SELECT u.`Name`
FROM (
  SELECT `Name`
  FROM `Researcher`
  UNION ALL
  SELECT `Name`
  FROM `Institution`
) AS u;
