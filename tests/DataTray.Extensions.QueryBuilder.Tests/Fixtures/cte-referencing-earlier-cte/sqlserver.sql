WITH [A] AS (
  SELECT [Name],
         [HIndex]
  FROM [Researcher]
),
[B] AS (
  SELECT [Name]
  FROM [A]
  WHERE [HIndex] > 5
)
SELECT [Name]
FROM [B];
