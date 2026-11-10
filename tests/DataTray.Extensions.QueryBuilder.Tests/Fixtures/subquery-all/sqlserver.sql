SELECT [Id]
FROM [Researcher]
WHERE [HIndex] >= ALL (
  SELECT [HIndex]
  FROM [Researcher]
  WHERE [InstitutionId] = 1
);
