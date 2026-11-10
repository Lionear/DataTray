SELECT [Id]
FROM [Researcher]
WHERE [Id] NOT IN (
  SELECT [ResearcherId]
  FROM [Grant]
);
