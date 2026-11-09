SELECT `Id`
FROM `Researcher`
WHERE `Id` IN (
  SELECT `ResearcherId`
  FROM `Grant`
  ORDER BY `Amount` DESC
);
