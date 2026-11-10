WITH `U` AS (
  SELECT `Name`
  FROM `Researcher`
  UNION
  SELECT `Name`
  FROM `Institution`
)
SELECT `Name`
FROM `U`;
