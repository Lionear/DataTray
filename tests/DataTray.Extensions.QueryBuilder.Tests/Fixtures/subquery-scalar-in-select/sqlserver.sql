SELECT r.[Name],
       (
         SELECT COUNT(*)
         FROM [Grant] AS g
         WHERE g.[ResearcherId] = r.[Id]
       ) AS [Grants]
FROM [Researcher] AS r;
