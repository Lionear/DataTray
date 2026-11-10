SELECT [Name],
       DENSE_RANK() OVER (ORDER BY [HIndex] DESC) AS [W]
FROM [Researcher];
