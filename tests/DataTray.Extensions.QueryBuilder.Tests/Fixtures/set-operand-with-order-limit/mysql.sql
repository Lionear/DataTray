(
  SELECT `Name`
  FROM `Researcher`
  ORDER BY `Name`
  LIMIT 3
)
UNION ALL
(
  SELECT `Name`
  FROM `Institution`
  ORDER BY `Name`
  LIMIT 3
);
