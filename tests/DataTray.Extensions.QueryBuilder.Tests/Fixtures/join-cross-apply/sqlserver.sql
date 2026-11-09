SELECT i.[Name],
       x.[Name]
FROM [Institution] AS i
CROSS APPLY (
  SELECT [Name]
  FROM [Researcher]
  WHERE [InstitutionId] = i.[Id]
) AS x;
