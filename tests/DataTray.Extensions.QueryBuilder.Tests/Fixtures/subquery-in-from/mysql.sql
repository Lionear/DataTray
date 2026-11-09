SELECT t.`Country`,
       t.`N`
FROM (
  SELECT `Country`,
         COUNT(*) AS `N`
  FROM `Institution`
  GROUP BY `Country`
) AS t
WHERE t.`N` > 1;
