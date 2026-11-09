WITH [R] AS (
  SELECT [Id],
         [Name],
         [AdvisorId]
  FROM [Researcher]
)
SELECT x.[Name],
       y.[Name]
FROM [R] AS x
INNER JOIN [R] AS y ON x.[AdvisorId] = y.[Id];
