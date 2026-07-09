-- 255_org_managers.sql — 標準組織の役職者（承認ルート用）seed（250/251 の後に適用）
-- 各部: 部長 + 部長代理 = director（部長権限・OR承認）、第一課長 + 第二課長 = manager（並列課長・OR承認）

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '20' AND u.user_name = 'eigyo_bucho'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '20' AND u.user_name = 'eigyo_buchodairi'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '20' AND u.user_name = 'eigyo_kacho1'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '20' AND u.user_name = 'eigyo_kacho2'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '31' AND u.user_name = 'kaihatsu1_bucho'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '31' AND u.user_name = 'kaihatsu1_buchodairi'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '31' AND u.user_name = 'kaihatsu1_kacho1'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '31' AND u.user_name = 'kaihatsu1_kacho2'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '32' AND u.user_name = 'kaihatsu2_bucho'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '32' AND u.user_name = 'kaihatsu2_buchodairi'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '32' AND u.user_name = 'kaihatsu2_kacho1'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '32' AND u.user_name = 'kaihatsu2_kacho2'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '40' AND u.user_name = 'saas_bucho'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '40' AND u.user_name = 'saas_buchodairi'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '40' AND u.user_name = 'saas_kacho1'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '40' AND u.user_name = 'saas_kacho2'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '10' AND u.user_name = 'soumu_bucho'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'director'
FROM departments d, app_users u
WHERE d.code = '10' AND u.user_name = 'soumu_buchodairi'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'director');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '10' AND u.user_name = 'soumu_kacho1'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, u.id, 'manager'
FROM departments d, app_users u
WHERE d.code = '10' AND u.user_name = 'soumu_kacho2'
  AND NOT EXISTS (SELECT 1 FROM department_managers x WHERE x.department_id = d.id AND x.user_id = u.id AND x.role = 'manager');

