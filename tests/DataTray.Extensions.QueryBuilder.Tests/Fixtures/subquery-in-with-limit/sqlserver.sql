SELECT [Id]
FROM [Researcher]
WHERE [Id] IN (
  SELECT TOP (3) [ResearcherId]
  FROM [Grant]
  ORDER BY [Amount] DESC
);
