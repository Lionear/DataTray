SELECT r.`Name`
FROM `Researcher` AS r
WHERE EXISTS (
  SELECT 1
  FROM `PaperAuthor` AS pa
  WHERE pa.`ResearcherId` = r.`Id` AND pa.`Position` = 1
);
