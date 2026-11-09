SELECT (
  SELECT TOP (1) [Title]
  FROM [Paper]
  ORDER BY [Citations] DESC
) AS [TopPaper];
