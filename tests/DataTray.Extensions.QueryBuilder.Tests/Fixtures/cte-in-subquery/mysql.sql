SELECT `Id`
FROM `Researcher`
WHERE `Id` IN (
  WITH `X` AS (
    SELECT `Id`
    FROM `Researcher`
  )
  SELECT `Id`
  FROM `X`
);
