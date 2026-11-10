SELECT r.[Name]
FROM [Institution] AS i
INNER JOIN ([Researcher] AS r INNER JOIN [Department] AS d ON r.[InstitutionId] = d.[InstitutionId]) ON i.[Id] = d.[InstitutionId];
