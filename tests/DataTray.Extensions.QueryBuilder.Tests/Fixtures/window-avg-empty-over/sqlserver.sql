SELECT [Name],
       AVG([Salary]) OVER () AS [W]
FROM [Researcher];
