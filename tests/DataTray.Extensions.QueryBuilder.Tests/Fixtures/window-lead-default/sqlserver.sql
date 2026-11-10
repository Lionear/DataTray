SELECT [Name],
       LEAD([HIndex], 1, 0) OVER (ORDER BY [Id]) AS [W]
FROM [Researcher];
