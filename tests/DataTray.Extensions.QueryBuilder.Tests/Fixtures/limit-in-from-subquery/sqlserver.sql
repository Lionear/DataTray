SELECT t.[Name]
FROM (
  SELECT TOP (2) [Name]
  FROM [Researcher]
  ORDER BY [Id]
) AS t;
