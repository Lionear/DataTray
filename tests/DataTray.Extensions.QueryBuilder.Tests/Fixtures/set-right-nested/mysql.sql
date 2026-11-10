SELECT `Name`
FROM `Researcher`
EXCEPT
(
  SELECT `Name`
  FROM `Institution`
  UNION
  SELECT `Name`
  FROM `Conference`
);
