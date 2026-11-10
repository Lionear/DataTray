SELECT [Name],
       SUM([Salary]) OVER w AS [Total],
       RANK() OVER w AS [Pos]
FROM [Researcher]
WINDOW w AS (PARTITION BY [InstitutionId] ORDER BY [HIndex] DESC);
