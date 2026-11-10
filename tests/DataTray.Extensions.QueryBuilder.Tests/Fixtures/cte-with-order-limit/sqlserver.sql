WITH [Top] AS (
  SELECT TOP (5) [Name]
  FROM [Researcher]
  ORDER BY [HIndex] DESC
)
SELECT [Name]
FROM [Top];
