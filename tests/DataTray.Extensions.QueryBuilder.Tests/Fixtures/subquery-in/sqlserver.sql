SELECT [Id]
FROM [Researcher]
WHERE [InstitutionId] IN (
  SELECT [Id]
  FROM [Institution]
  WHERE [Country] = N'NL'
);
