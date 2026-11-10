SELECT CASE WHEN "HIndex" > 30 THEN 'high' WHEN "HIndex" > 10 THEN 'mid' ELSE 'low' END AS "Level"
FROM "Researcher";
