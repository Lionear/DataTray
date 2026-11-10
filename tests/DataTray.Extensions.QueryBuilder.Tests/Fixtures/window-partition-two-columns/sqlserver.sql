SELECT [Name],
       SUM([Salary]) OVER (PARTITION BY [InstitutionId], [Active] ORDER BY [Id]) AS [W]
FROM [Researcher];
