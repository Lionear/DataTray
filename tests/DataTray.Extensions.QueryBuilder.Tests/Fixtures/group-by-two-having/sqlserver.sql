SELECT [InstitutionId],
       [Active],
       SUM([Salary]) AS [Total]
FROM [Researcher]
GROUP BY [InstitutionId], [Active]
HAVING SUM([Salary]) > 1000;
