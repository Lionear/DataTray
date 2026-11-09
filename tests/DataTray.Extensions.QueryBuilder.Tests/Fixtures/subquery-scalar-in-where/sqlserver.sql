SELECT [Id]
FROM [Researcher]
WHERE [HIndex] > (
  SELECT AVG([HIndex])
  FROM [Researcher]
);
