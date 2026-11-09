SELECT [Name],
       SUM([Salary]) OVER (ORDER BY [Id] ROWS 3 PRECEDING) AS [W]
FROM [Researcher];
