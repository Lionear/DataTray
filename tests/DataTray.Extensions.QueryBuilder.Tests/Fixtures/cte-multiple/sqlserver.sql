WITH [A] AS (
  SELECT [Name],
         [InstitutionId]
  FROM [Researcher]
),
[B] AS (
  SELECT [Id],
         [Country]
  FROM [Institution]
)
SELECT a.[Name],
       b.[Country]
FROM [A] AS a
INNER JOIN [B] AS b ON a.[InstitutionId] = b.[Id];
