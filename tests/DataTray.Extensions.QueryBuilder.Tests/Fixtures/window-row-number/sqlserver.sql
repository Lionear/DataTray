SELECT [Name],
       ROW_NUMBER() OVER (PARTITION BY [InstitutionId] ORDER BY [HIndex] DESC) AS [W]
FROM [Researcher];
