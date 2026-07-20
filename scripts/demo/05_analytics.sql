-- pgNimbus demo data — 05: the `analytics` schema (views, matviews, functions).
--
-- Depends on 01-04. Adds plain views, materialized views (with unique indexes
-- so REFRESH CONCURRENTLY is possible), sql/plpgsql functions and a trigger,
-- then refreshes the matviews and runs ANALYZE so the Database Overview panel
-- has real planner stats. Safe to re-run (analytics schema is dropped/recreated).

BEGIN;

DROP SCHEMA IF EXISTS analytics CASCADE;
CREATE SCHEMA analytics;

-- --- Plain views -----------------------------------------------------------
CREATE VIEW analytics.v_order_totals AS
SELECT
    o.id         AS order_id,
    o.order_no,
    o.customer_id,
    o.status,
    o.placed_at,
    count(oi.product_id)          AS line_count,
    coalesce(sum(oi.quantity), 0) AS units,
    coalesce(sum(oi.quantity * oi.unit_price * (1 - oi.discount)), 0)::numeric(14,2) AS computed_total
FROM commerce.orders o
LEFT JOIN commerce.order_items oi ON oi.order_id = o.id
GROUP BY o.id, o.order_no, o.customer_id, o.status, o.placed_at;
COMMENT ON VIEW analytics.v_order_totals IS 'Per-order totals computed from line items (plain view).';

CREATE VIEW analytics.v_customer_spend AS
SELECT
    c.id,
    c.full_name,
    c.email,
    count(o.id)                        AS order_count,
    coalesce(sum(t.computed_total), 0) AS lifetime_value,
    max(o.placed_at)                   AS last_order_at
FROM commerce.customers c
LEFT JOIN commerce.orders o          ON o.customer_id = c.id
LEFT JOIN analytics.v_order_totals t ON t.order_id = o.id
GROUP BY c.id, c.full_name, c.email;
COMMENT ON VIEW analytics.v_customer_spend IS 'Customer lifetime value, built on top of another view.';

-- --- Materialized views (with refresh-friendly unique indexes) --------------
CREATE MATERIALIZED VIEW analytics.mv_daily_sales AS
SELECT
    date_trunc('day', o.placed_at)::date AS sales_day,
    count(*)                             AS orders,
    sum(t.units)                         AS units,
    sum(t.computed_total)::numeric(14,2) AS revenue
FROM commerce.orders o
JOIN analytics.v_order_totals t ON t.order_id = o.id
GROUP BY 1
WITH DATA;
CREATE UNIQUE INDEX ux_mv_daily_sales_day ON analytics.mv_daily_sales (sales_day);
COMMENT ON MATERIALIZED VIEW analytics.mv_daily_sales IS 'Daily order/revenue rollup (materialized; REFRESH CONCURRENTLY-ready via unique index).';

CREATE MATERIALIZED VIEW analytics.mv_top_products AS
SELECT
    p.id       AS product_id,
    p.name,
    p.category,
    count(DISTINCT oi.order_id)                     AS orders,
    sum(oi.quantity)                                AS units_sold,
    sum(oi.quantity * oi.unit_price)::numeric(14,2) AS gross_revenue,
    round(avg(r.rating), 2)                         AS avg_rating
FROM commerce.products p
LEFT JOIN commerce.order_items oi ON oi.product_id = p.id
LEFT JOIN commerce.reviews r      ON r.product_id = p.id
GROUP BY p.id, p.name, p.category
WITH DATA;
CREATE UNIQUE INDEX ux_mv_top_products_id ON analytics.mv_top_products (product_id);
COMMENT ON MATERIALIZED VIEW analytics.mv_top_products IS 'Best-selling products by revenue, joined with average review rating.';

-- --- Functions -------------------------------------------------------------
CREATE OR REPLACE FUNCTION commerce.apply_discount(price numeric, pct numeric)
RETURNS numeric
LANGUAGE sql IMMUTABLE STRICT AS $$
    SELECT round(price * (1 - pct), 2);
$$;
COMMENT ON FUNCTION commerce.apply_discount IS 'Immutable helper: price after a fractional discount.';

CREATE OR REPLACE FUNCTION analytics.customer_ltv(p_customer uuid)
RETURNS numeric
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_total numeric;
BEGIN
    SELECT coalesce(sum(oi.quantity * oi.unit_price * (1 - oi.discount)), 0)
      INTO v_total
      FROM commerce.orders o
      JOIN commerce.order_items oi ON oi.order_id = o.id
     WHERE o.customer_id = p_customer;
    RETURN round(v_total, 2);
END;
$$;
COMMENT ON FUNCTION analytics.customer_ltv IS 'Lifetime value for one customer (plpgsql).';

-- Set-returning function using ltree descendant match
CREATE OR REPLACE FUNCTION org.reports_under(p_path ltree)
RETURNS TABLE (employee_id int, employee_name text, unit_name text, salary money)
LANGUAGE sql STABLE AS $$
    SELECT e.id, e.name, u.name, e.salary
    FROM org.employees e
    JOIN org.units u ON u.id = e.unit_id
    WHERE u.path <@ p_path
    ORDER BY e.salary DESC;
$$;
COMMENT ON FUNCTION org.reports_under IS 'All employees within an org subtree (ltree descendant match).';

-- Trigger function + trigger (keeps public.products.updated_at fresh)
CREATE OR REPLACE FUNCTION public.touch_updated_at()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER trg_products_touch
    BEFORE UPDATE ON public.products
    FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();

COMMIT;

-- Populate the matviews and freshen planner stats (outside the transaction).
REFRESH MATERIALIZED VIEW analytics.mv_daily_sales;
REFRESH MATERIALIZED VIEW analytics.mv_top_products;
ANALYZE;
