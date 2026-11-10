(
  SELECT [Name]
  FROM [Researcher]
  UNION ALL
  SELECT [Name]
  FROM [Institution]
)
INTERSECT
SELECT [Name]
FROM [Conference];
