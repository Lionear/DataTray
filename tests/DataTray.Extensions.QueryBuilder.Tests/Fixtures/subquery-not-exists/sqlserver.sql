SELECT r.[Id]
FROM [Researcher] AS r
WHERE NOT EXISTS (
  SELECT 1
  FROM [Grant] AS g
  WHERE g.[ResearcherId] = r.[Id]
);
