SELECT p.*
FROM [Paper] AS p
INNER JOIN [Conference] AS c ON p.[ConferenceId] = c.[Id]
WHERE c.[Rank] = N'A*'
ORDER BY p.[Citations] DESC;
