SELECT [Year] + 0 AS [Y],
       COUNT(*) AS [N]
FROM [Paper]
GROUP BY [Year] + 0;
