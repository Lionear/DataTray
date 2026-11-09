WITH [Chain] ([Id], [Name], [Depth]) AS (
  SELECT [Id],
         [Name],
         0
  FROM [Researcher]
  WHERE [AdvisorId] IS NULL
  UNION ALL
  SELECT r.[Id],
         r.[Name],
         c.[Depth] + 1
  FROM [Researcher] AS r
  INNER JOIN [Chain] AS c ON r.[AdvisorId] = c.[Id]
)
SELECT [Name],
       [Depth]
FROM [Chain];
