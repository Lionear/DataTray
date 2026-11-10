SELECT r.[Name],
       p.[Title]
FROM [Researcher] AS r
INNER JOIN [PaperAuthor] AS pa ON r.[Id] = pa.[ResearcherId]
LEFT JOIN [Paper] AS p ON pa.[PaperId] = p.[Id];
