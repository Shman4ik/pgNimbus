SELECT c.first_name, c.last_name, c.email
FROM customers c
WHERE c.is_active = true
ORDER BY c.last_name
---
SELECT o.id, o.order_date, o.total_amount
FROM orders o
JOIN customers c ON c.id = o.customer_id
WHERE o.status = 'paid'
ORDER BY o.order_date DESC
LIMIT 50
---
SELECT c.email, count(*) AS orders, sum(o.total_amount) AS spent
FROM customers c
LEFT JOIN orders o ON o.customer_id = c.id
GROUP BY c.email
HAVING count(*) > 3
ORDER BY spent DESC
---
SELECT p.title, sum(oi.quantity) AS sold
FROM order_items oi
JOIN products p ON p.id = oi.product_id
GROUP BY p.title
ORDER BY sold DESC
LIMIT 10
---
SELECT *
FROM orders
WHERE customer_id = 42
AND order_date > now() - interval '30 days'
---
UPDATE customers
SET is_active = false
WHERE created_at < now() - interval '2 years'
---
DELETE FROM order_items
WHERE order_id IN (SELECT id FROM orders WHERE status = 'cancelled')
---
INSERT INTO customers (first_name, last_name, email)
VALUES ('Ada', 'Lovelace', 'ada@example.com')
RETURNING id
---
SELECT i.number, i.title, u.email
FROM saas.issues i
JOIN saas.users u ON u.id = i.assignee_id
WHERE i.status = 'open'
AND i.priority <= 2
ORDER BY i.priority, i.created_at
---
SELECT a.name, count(u.id) AS users
FROM saas.accounts a
LEFT JOIN saas.users u ON u.account_id = a.id
WHERE a.deleted_at IS NULL
GROUP BY a.name
ORDER BY users DESC
---
SELECT p.key, p.name, count(i.id) AS open_issues
FROM saas.projects p
JOIN saas.issues i ON i.project_id = p.id
WHERE i.closed_at IS NULL
AND p.archived = false
GROUP BY p.key, p.name
---
WITH recent AS (
SELECT customer_id, max(order_date) AS last_order
FROM orders
GROUP BY customer_id
)
SELECT c.email, r.last_order
FROM customers c
JOIN recent r ON r.customer_id = c.id
WHERE r.last_order < now() - interval '90 days'
---
SELECT s.status, count(*)
FROM saas.subscriptions s
JOIN saas.plans pl ON pl.id = s.plan_id
WHERE pl.tier = 'pro'
GROUP BY s.status
---
SELECT inv.number, inv.total, inv.paid_at
FROM saas.invoices inv
WHERE inv.paid_at IS NULL
AND inv.due_at < current_date
ORDER BY inv.due_at
---
SELECT u.email, u.last_login_at
FROM saas.users u
WHERE u.account_id = 7
AND u.is_admin
---
SELECT e.name, m.name AS manager
FROM org.employees e
LEFT JOIN org.employees m ON m.id = e.manager_id
ORDER BY e.name
---
SELECT d.name, avg(r.value) AS avg_value
FROM iot.devices d
JOIN iot.readings r ON r.device_id = d.id
WHERE r.recorded_at > now() - interval '1 day'
GROUP BY d.name
---
UPDATE saas.issues
SET status = 'done', closed_at = now()
WHERE id = 1001
---
SELECT count(*)
FROM saas.audit_events ae
WHERE ae.action = 'login'
AND ae.occurred_at >= date_trunc('month', now())
---
SELECT t.name, tm.role, u.email
FROM saas.teams t
JOIN saas.team_members tm ON tm.team_id = t.id
JOIN saas.users u ON u.id = tm.user_id
WHERE t.account_id = 3
---
CREATE INDEX ON orders (customer_id, order_date)
---
ALTER TABLE customers ADD COLUMN phone text
---
SELECT column_name, data_type
FROM information_schema.columns
WHERE table_name = 'orders'
---
SELECT pid, state, query
FROM pg_stat_activity
WHERE state <> 'idle'
---
SELECT relname, pg_size_pretty(pg_total_relation_size(oid))
FROM pg_class
WHERE relkind = 'r'
ORDER BY pg_total_relation_size(oid) DESC
LIMIT 20
---
SELECT o.customer_id, o.total_amount, row_number() OVER (PARTITION BY o.customer_id ORDER BY o.order_date DESC) AS rn
FROM orders o
WHERE o.status IS NOT NULL
ORDER BY o.customer_id, rn
---
SELECT i.status, count(*) FILTER (WHERE i.priority <= 2) AS urgent, count(*) AS total
FROM saas.issues i
GROUP BY i.status
ORDER BY total DESC NULLS LAST
---
SELECT p.title, CASE WHEN p.stock_quantity > 0 THEN 'in stock' ELSE 'sold out' END AS availability
FROM products p
ORDER BY p.title
---
INSERT INTO customers (email, first_name)
VALUES ('grace@example.com', 'Grace')
ON CONFLICT (email) DO UPDATE SET first_name = excluded.first_name
---
MERGE INTO customers c
USING saas.users u ON u.email = c.email
WHEN MATCHED THEN UPDATE SET is_active = true
WHEN NOT MATCHED THEN DO NOTHING
---
CREATE TABLE saas.labels (
id bigint PRIMARY KEY,
name text NOT NULL,
issue_id bigint REFERENCES saas.issues (id)
)
---
GRANT SELECT ON saas.issues TO postgres
---
SET statement_timeout TO '5s'
---
VACUUM ANALYZE orders
---
DROP VIEW IF EXISTS saas.account_seats
---
ALTER TABLE customers ALTER COLUMN email SET NOT NULL
