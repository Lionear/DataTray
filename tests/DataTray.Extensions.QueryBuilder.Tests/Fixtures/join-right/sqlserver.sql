SELECT r.[Name],
       i.[Name]
FROM [Researcher] AS r
RIGHT JOIN [Institution] AS i ON r.[InstitutionId] = i.[Id];
