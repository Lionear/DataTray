WITH `Senior` AS (
  SELECT `Name`
  FROM `Researcher`
  WHERE `HIndex` > 20
)
SELECT `Name`
FROM `Senior`;
