SELECT CASE WHEN [HIndex] > 30 THEN N'high' WHEN [HIndex] > 10 THEN N'mid' ELSE N'low' END AS [Level]
FROM [Researcher];
