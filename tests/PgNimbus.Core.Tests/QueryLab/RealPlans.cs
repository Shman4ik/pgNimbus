namespace PgNimbus.Core.Tests.QueryLab;

/// <summary>
/// Real plans from PostgreSQL 17.11, captured with <c>EXPLAIN (ANALYZE, BUFFERS, SETTINGS)</c>
/// for one statement over 400,000 orders and 10,000 customers:
/// <code>
/// SELECT c.name, count(*) AS orders, sum(o.amount) AS total
/// FROM orders o JOIN customers c ON c.id = o.customer_id
/// WHERE o.customer_id = 42 GROUP BY c.name
/// </code>
/// Before is without an index on <c>orders.customer_id</c> (a Gather over a Parallel Seq
/// Scan), after is with one (a Bitmap Heap Scan). The text forms are psql's output, framing
/// included; <see cref="BeforeColdJson"/> is the before plan run first after a restart, with
/// shared buffers empty.
/// </summary>
internal static class RealPlans
{
    public const string BeforeJson = """
        [
          {
            "Plan": {
              "Node Type": "Aggregate",
              "Strategy": "Sorted",
              "Partial Mode": "Simple",
              "Parallel Aware": false,
              "Async Capable": false,
              "Startup Cost": 6896.94,
              "Total Cost": 6897.36,
              "Plan Rows": 1,
              "Plan Width": 53,
              "Actual Startup Time": 9.256,
              "Actual Total Time": 11.330,
              "Actual Rows": 1,
              "Actual Loops": 1,
              "Group Key": ["c.name"],
              "Shared Hit Blocks": 2948,
              "Shared Read Blocks": 0,
              "Shared Dirtied Blocks": 0,
              "Shared Written Blocks": 0,
              "Local Hit Blocks": 0,
              "Local Read Blocks": 0,
              "Local Dirtied Blocks": 0,
              "Local Written Blocks": 0,
              "Temp Read Blocks": 0,
              "Temp Written Blocks": 0,
              "Plans": [
                {
                  "Node Type": "Sort",
                  "Parent Relationship": "Outer",
                  "Parallel Aware": false,
                  "Async Capable": false,
                  "Startup Cost": 6896.94,
                  "Total Cost": 6897.04,
                  "Plan Rows": 40,
                  "Plan Width": 19,
                  "Actual Startup Time": 9.193,
                  "Actual Total Time": 11.269,
                  "Actual Rows": 40,
                  "Actual Loops": 1,
                  "Sort Key": ["c.name"],
                  "Sort Method": "quicksort",
                  "Sort Space Used": 26,
                  "Sort Space Type": "Memory",
                  "Shared Hit Blocks": 2948,
                  "Shared Read Blocks": 0,
                  "Shared Dirtied Blocks": 0,
                  "Shared Written Blocks": 0,
                  "Local Hit Blocks": 0,
                  "Local Read Blocks": 0,
                  "Local Dirtied Blocks": 0,
                  "Local Written Blocks": 0,
                  "Temp Read Blocks": 0,
                  "Temp Written Blocks": 0,
                  "Plans": [
                    {
                      "Node Type": "Nested Loop",
                      "Parent Relationship": "Outer",
                      "Parallel Aware": false,
                      "Async Capable": false,
                      "Join Type": "Inner",
                      "Startup Cost": 1000.28,
                      "Total Cost": 6895.88,
                      "Plan Rows": 40,
                      "Plan Width": 19,
                      "Actual Startup Time": 0.507,
                      "Actual Total Time": 11.236,
                      "Actual Rows": 40,
                      "Actual Loops": 1,
                      "Inner Unique": false,
                      "Shared Hit Blocks": 2945,
                      "Shared Read Blocks": 0,
                      "Shared Dirtied Blocks": 0,
                      "Shared Written Blocks": 0,
                      "Local Hit Blocks": 0,
                      "Local Read Blocks": 0,
                      "Local Dirtied Blocks": 0,
                      "Local Written Blocks": 0,
                      "Temp Read Blocks": 0,
                      "Temp Written Blocks": 0,
                      "Plans": [
                        {
                          "Node Type": "Index Scan",
                          "Parent Relationship": "Outer",
                          "Parallel Aware": false,
                          "Async Capable": false,
                          "Scan Direction": "Forward",
                          "Index Name": "customers_pkey",
                          "Relation Name": "customers",
                          "Alias": "c",
                          "Startup Cost": 0.29,
                          "Total Cost": 8.30,
                          "Plan Rows": 1,
                          "Plan Width": 17,
                          "Actual Startup Time": 0.009,
                          "Actual Total Time": 0.011,
                          "Actual Rows": 1,
                          "Actual Loops": 1,
                          "Index Cond": "(id = 42)",
                          "Rows Removed by Index Recheck": 0,
                          "Shared Hit Blocks": 3,
                          "Shared Read Blocks": 0,
                          "Shared Dirtied Blocks": 0,
                          "Shared Written Blocks": 0,
                          "Local Hit Blocks": 0,
                          "Local Read Blocks": 0,
                          "Local Dirtied Blocks": 0,
                          "Local Written Blocks": 0,
                          "Temp Read Blocks": 0,
                          "Temp Written Blocks": 0
                        },
                        {
                          "Node Type": "Gather",
                          "Parent Relationship": "Inner",
                          "Parallel Aware": false,
                          "Async Capable": false,
                          "Startup Cost": 1000.00,
                          "Total Cost": 6887.18,
                          "Plan Rows": 40,
                          "Plan Width": 10,
                          "Actual Startup Time": 0.497,
                          "Actual Total Time": 11.218,
                          "Actual Rows": 40,
                          "Actual Loops": 1,
                          "Workers Planned": 1,
                          "Workers Launched": 1,
                          "Single Copy": false,
                          "Shared Hit Blocks": 2942,
                          "Shared Read Blocks": 0,
                          "Shared Dirtied Blocks": 0,
                          "Shared Written Blocks": 0,
                          "Local Hit Blocks": 0,
                          "Local Read Blocks": 0,
                          "Local Dirtied Blocks": 0,
                          "Local Written Blocks": 0,
                          "Temp Read Blocks": 0,
                          "Temp Written Blocks": 0,
                          "Plans": [
                            {
                              "Node Type": "Seq Scan",
                              "Parent Relationship": "Outer",
                              "Parallel Aware": true,
                              "Async Capable": false,
                              "Relation Name": "orders",
                              "Alias": "o",
                              "Startup Cost": 0.00,
                              "Total Cost": 5883.18,
                              "Plan Rows": 24,
                              "Plan Width": 10,
                              "Actual Startup Time": 0.266,
                              "Actual Total Time": 7.635,
                              "Actual Rows": 20,
                              "Actual Loops": 2,
                              "Filter": "(customer_id = 42)",
                              "Rows Removed by Filter": 199980,
                              "Shared Hit Blocks": 2942,
                              "Shared Read Blocks": 0,
                              "Shared Dirtied Blocks": 0,
                              "Shared Written Blocks": 0,
                              "Local Hit Blocks": 0,
                              "Local Read Blocks": 0,
                              "Local Dirtied Blocks": 0,
                              "Local Written Blocks": 0,
                              "Temp Read Blocks": 0,
                              "Temp Written Blocks": 0,
                              "Workers": [
                              ]
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            },
            "Settings": {
            },
            "Planning": {
              "Shared Hit Blocks": 173,
              "Shared Read Blocks": 1,
              "Shared Dirtied Blocks": 0,
              "Shared Written Blocks": 0,
              "Local Hit Blocks": 0,
              "Local Read Blocks": 0,
              "Local Dirtied Blocks": 0,
              "Local Written Blocks": 0,
              "Temp Read Blocks": 0,
              "Temp Written Blocks": 0
            },
            "Planning Time": 9.712,
            "Triggers": [
            ],
            "Execution Time": 11.406
          }
        ]
        """;

    public const string BeforeText = """
                                                                          QUERY PLAN                                                                   
        -----------------------------------------------------------------------------------------------------------------------------------------------
         GroupAggregate  (cost=6896.94..6897.36 rows=1 width=53) (actual time=10.998..13.363 rows=1 loops=1)
           Group Key: c.name
           Buffers: shared hit=2948
           ->  Sort  (cost=6896.94..6897.04 rows=40 width=19) (actual time=10.978..13.344 rows=40 loops=1)
                 Sort Key: c.name
                 Sort Method: quicksort  Memory: 26kB
                 Buffers: shared hit=2948
                 ->  Nested Loop  (cost=1000.28..6895.88 rows=40 width=19) (actual time=0.707..13.306 rows=40 loops=1)
                       Buffers: shared hit=2945
                       ->  Index Scan using customers_pkey on customers c  (cost=0.29..8.30 rows=1 width=17) (actual time=0.007..0.010 rows=1 loops=1)
                             Index Cond: (id = 42)
                             Buffers: shared hit=3
                       ->  Gather  (cost=1000.00..6887.18 rows=40 width=10) (actual time=0.699..13.288 rows=40 loops=1)
                             Workers Planned: 1
                             Workers Launched: 1
                             Buffers: shared hit=2942
                             ->  Parallel Seq Scan on orders o  (cost=0.00..5883.18 rows=24 width=10) (actual time=0.226..8.848 rows=20 loops=2)
                                   Filter: (customer_id = 42)
                                   Rows Removed by Filter: 199980
                                   Buffers: shared hit=2942
         Planning:
           Buffers: shared hit=174
         Planning Time: 0.405 ms
         Execution Time: 13.463 ms
        (24 rows)
        """;

    public const string AfterJson = """
        [
          {
            "Plan": {
              "Node Type": "Aggregate",
              "Strategy": "Sorted",
              "Partial Mode": "Simple",
              "Parallel Aware": false,
              "Async Capable": false,
              "Startup Cost": 161.01,
              "Total Cost": 161.42,
              "Plan Rows": 1,
              "Plan Width": 53,
              "Actual Startup Time": 0.293,
              "Actual Total Time": 0.294,
              "Actual Rows": 1,
              "Actual Loops": 1,
              "Group Key": ["c.name"],
              "Shared Hit Blocks": 46,
              "Shared Read Blocks": 3,
              "Shared Dirtied Blocks": 0,
              "Shared Written Blocks": 0,
              "Local Hit Blocks": 0,
              "Local Read Blocks": 0,
              "Local Dirtied Blocks": 0,
              "Local Written Blocks": 0,
              "Temp Read Blocks": 0,
              "Temp Written Blocks": 0,
              "Plans": [
                {
                  "Node Type": "Sort",
                  "Parent Relationship": "Outer",
                  "Parallel Aware": false,
                  "Async Capable": false,
                  "Startup Cost": 161.01,
                  "Total Cost": 161.11,
                  "Plan Rows": 40,
                  "Plan Width": 19,
                  "Actual Startup Time": 0.278,
                  "Actual Total Time": 0.280,
                  "Actual Rows": 40,
                  "Actual Loops": 1,
                  "Sort Key": ["c.name"],
                  "Sort Method": "quicksort",
                  "Sort Space Used": 26,
                  "Sort Space Type": "Memory",
                  "Shared Hit Blocks": 46,
                  "Shared Read Blocks": 3,
                  "Shared Dirtied Blocks": 0,
                  "Shared Written Blocks": 0,
                  "Local Hit Blocks": 0,
                  "Local Read Blocks": 0,
                  "Local Dirtied Blocks": 0,
                  "Local Written Blocks": 0,
                  "Temp Read Blocks": 0,
                  "Temp Written Blocks": 0,
                  "Plans": [
                    {
                      "Node Type": "Nested Loop",
                      "Parent Relationship": "Outer",
                      "Parallel Aware": false,
                      "Async Capable": false,
                      "Join Type": "Inner",
                      "Startup Cost": 5.02,
                      "Total Cost": 159.94,
                      "Plan Rows": 40,
                      "Plan Width": 19,
                      "Actual Startup Time": 0.055,
                      "Actual Total Time": 0.255,
                      "Actual Rows": 40,
                      "Actual Loops": 1,
                      "Inner Unique": false,
                      "Shared Hit Blocks": 43,
                      "Shared Read Blocks": 3,
                      "Shared Dirtied Blocks": 0,
                      "Shared Written Blocks": 0,
                      "Local Hit Blocks": 0,
                      "Local Read Blocks": 0,
                      "Local Dirtied Blocks": 0,
                      "Local Written Blocks": 0,
                      "Temp Read Blocks": 0,
                      "Temp Written Blocks": 0,
                      "Plans": [
                        {
                          "Node Type": "Index Scan",
                          "Parent Relationship": "Outer",
                          "Parallel Aware": false,
                          "Async Capable": false,
                          "Scan Direction": "Forward",
                          "Index Name": "customers_pkey",
                          "Relation Name": "customers",
                          "Alias": "c",
                          "Startup Cost": 0.29,
                          "Total Cost": 8.30,
                          "Plan Rows": 1,
                          "Plan Width": 17,
                          "Actual Startup Time": 0.007,
                          "Actual Total Time": 0.008,
                          "Actual Rows": 1,
                          "Actual Loops": 1,
                          "Index Cond": "(id = 42)",
                          "Rows Removed by Index Recheck": 0,
                          "Shared Hit Blocks": 3,
                          "Shared Read Blocks": 0,
                          "Shared Dirtied Blocks": 0,
                          "Shared Written Blocks": 0,
                          "Local Hit Blocks": 0,
                          "Local Read Blocks": 0,
                          "Local Dirtied Blocks": 0,
                          "Local Written Blocks": 0,
                          "Temp Read Blocks": 0,
                          "Temp Written Blocks": 0
                        },
                        {
                          "Node Type": "Bitmap Heap Scan",
                          "Parent Relationship": "Inner",
                          "Parallel Aware": false,
                          "Async Capable": false,
                          "Relation Name": "orders",
                          "Alias": "o",
                          "Startup Cost": 4.73,
                          "Total Cost": 151.24,
                          "Plan Rows": 40,
                          "Plan Width": 10,
                          "Actual Startup Time": 0.045,
                          "Actual Total Time": 0.239,
                          "Actual Rows": 40,
                          "Actual Loops": 1,
                          "Recheck Cond": "(customer_id = 42)",
                          "Rows Removed by Index Recheck": 0,
                          "Exact Heap Blocks": 40,
                          "Lossy Heap Blocks": 0,
                          "Shared Hit Blocks": 40,
                          "Shared Read Blocks": 3,
                          "Shared Dirtied Blocks": 0,
                          "Shared Written Blocks": 0,
                          "Local Hit Blocks": 0,
                          "Local Read Blocks": 0,
                          "Local Dirtied Blocks": 0,
                          "Local Written Blocks": 0,
                          "Temp Read Blocks": 0,
                          "Temp Written Blocks": 0,
                          "Plans": [
                            {
                              "Node Type": "Bitmap Index Scan",
                              "Parent Relationship": "Outer",
                              "Parallel Aware": false,
                              "Async Capable": false,
                              "Index Name": "orders_customer_id_idx",
                              "Startup Cost": 0.00,
                              "Total Cost": 4.72,
                              "Plan Rows": 40,
                              "Plan Width": 0,
                              "Actual Startup Time": 0.035,
                              "Actual Total Time": 0.035,
                              "Actual Rows": 40,
                              "Actual Loops": 1,
                              "Index Cond": "(customer_id = 42)",
                              "Shared Hit Blocks": 0,
                              "Shared Read Blocks": 3,
                              "Shared Dirtied Blocks": 0,
                              "Shared Written Blocks": 0,
                              "Local Hit Blocks": 0,
                              "Local Read Blocks": 0,
                              "Local Dirtied Blocks": 0,
                              "Local Written Blocks": 0,
                              "Temp Read Blocks": 0,
                              "Temp Written Blocks": 0
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            },
            "Settings": {
            },
            "Planning": {
              "Shared Hit Blocks": 170,
              "Shared Read Blocks": 1,
              "Shared Dirtied Blocks": 0,
              "Shared Written Blocks": 0,
              "Local Hit Blocks": 0,
              "Local Read Blocks": 0,
              "Local Dirtied Blocks": 0,
              "Local Written Blocks": 0,
              "Temp Read Blocks": 0,
              "Temp Written Blocks": 0
            },
            "Planning Time": 0.532,
            "Triggers": [
            ],
            "Execution Time": 0.365
          }
        ]
        """;

    public const string AfterText = """
                                                                            QUERY PLAN                                                                     
        ---------------------------------------------------------------------------------------------------------------------------------------------------
         GroupAggregate  (cost=161.01..161.42 rows=1 width=53) (actual time=0.211..0.212 rows=1 loops=1)
           Group Key: c.name
           Buffers: shared hit=49
           ->  Sort  (cost=161.01..161.11 rows=40 width=19) (actual time=0.197..0.199 rows=40 loops=1)
                 Sort Key: c.name
                 Sort Method: quicksort  Memory: 26kB
                 Buffers: shared hit=49
                 ->  Nested Loop  (cost=5.02..159.94 rows=40 width=19) (actual time=0.027..0.177 rows=40 loops=1)
                       Buffers: shared hit=46
                       ->  Index Scan using customers_pkey on customers c  (cost=0.29..8.30 rows=1 width=17) (actual time=0.007..0.007 rows=1 loops=1)
                             Index Cond: (id = 42)
                             Buffers: shared hit=3
                       ->  Bitmap Heap Scan on orders o  (cost=4.73..151.24 rows=40 width=10) (actual time=0.018..0.164 rows=40 loops=1)
                             Recheck Cond: (customer_id = 42)
                             Heap Blocks: exact=40
                             Buffers: shared hit=43
                             ->  Bitmap Index Scan on orders_customer_id_idx  (cost=0.00..4.72 rows=40 width=0) (actual time=0.009..0.009 rows=40 loops=1)
                                   Index Cond: (customer_id = 42)
                                   Buffers: shared hit=3
         Planning:
           Buffers: shared hit=171
         Planning Time: 0.324 ms
         Execution Time: 0.281 ms
        (23 rows)
        """;

    public const string BeforeColdJson = """
        [
          {
            "Plan": {
              "Node Type": "Aggregate",
              "Strategy": "Sorted",
              "Partial Mode": "Simple",
              "Parallel Aware": false,
              "Async Capable": false,
              "Startup Cost": 6896.94,
              "Total Cost": 6897.36,
              "Plan Rows": 1,
              "Plan Width": 53,
              "Actual Startup Time": 23.137,
              "Actual Total Time": 24.950,
              "Actual Rows": 1,
              "Actual Loops": 1,
              "Group Key": ["c.name"],
              "Shared Hit Blocks": 3,
              "Shared Read Blocks": 2945,
              "Shared Dirtied Blocks": 0,
              "Shared Written Blocks": 0,
              "Local Hit Blocks": 0,
              "Local Read Blocks": 0,
              "Local Dirtied Blocks": 0,
              "Local Written Blocks": 0,
              "Temp Read Blocks": 0,
              "Temp Written Blocks": 0,
              "Plans": [
                {
                  "Node Type": "Sort",
                  "Parent Relationship": "Outer",
                  "Parallel Aware": false,
                  "Async Capable": false,
                  "Startup Cost": 6896.94,
                  "Total Cost": 6897.04,
                  "Plan Rows": 40,
                  "Plan Width": 19,
                  "Actual Startup Time": 23.121,
                  "Actual Total Time": 24.935,
                  "Actual Rows": 40,
                  "Actual Loops": 1,
                  "Sort Key": ["c.name"],
                  "Sort Method": "quicksort",
                  "Sort Space Used": 26,
                  "Sort Space Type": "Memory",
                  "Shared Hit Blocks": 3,
                  "Shared Read Blocks": 2945,
                  "Shared Dirtied Blocks": 0,
                  "Shared Written Blocks": 0,
                  "Local Hit Blocks": 0,
                  "Local Read Blocks": 0,
                  "Local Dirtied Blocks": 0,
                  "Local Written Blocks": 0,
                  "Temp Read Blocks": 0,
                  "Temp Written Blocks": 0,
                  "Plans": [
                    {
                      "Node Type": "Nested Loop",
                      "Parent Relationship": "Outer",
                      "Parallel Aware": false,
                      "Async Capable": false,
                      "Join Type": "Inner",
                      "Startup Cost": 1000.28,
                      "Total Cost": 6895.88,
                      "Plan Rows": 40,
                      "Plan Width": 19,
                      "Actual Startup Time": 1.078,
                      "Actual Total Time": 24.860,
                      "Actual Rows": 40,
                      "Actual Loops": 1,
                      "Inner Unique": false,
                      "Shared Hit Blocks": 0,
                      "Shared Read Blocks": 2945,
                      "Shared Dirtied Blocks": 0,
                      "Shared Written Blocks": 0,
                      "Local Hit Blocks": 0,
                      "Local Read Blocks": 0,
                      "Local Dirtied Blocks": 0,
                      "Local Written Blocks": 0,
                      "Temp Read Blocks": 0,
                      "Temp Written Blocks": 0,
                      "Plans": [
                        {
                          "Node Type": "Index Scan",
                          "Parent Relationship": "Outer",
                          "Parallel Aware": false,
                          "Async Capable": false,
                          "Scan Direction": "Forward",
                          "Index Name": "customers_pkey",
                          "Relation Name": "customers",
                          "Alias": "c",
                          "Startup Cost": 0.29,
                          "Total Cost": 8.30,
                          "Plan Rows": 1,
                          "Plan Width": 17,
                          "Actual Startup Time": 0.030,
                          "Actual Total Time": 0.032,
                          "Actual Rows": 1,
                          "Actual Loops": 1,
                          "Index Cond": "(id = 42)",
                          "Rows Removed by Index Recheck": 0,
                          "Shared Hit Blocks": 0,
                          "Shared Read Blocks": 3,
                          "Shared Dirtied Blocks": 0,
                          "Shared Written Blocks": 0,
                          "Local Hit Blocks": 0,
                          "Local Read Blocks": 0,
                          "Local Dirtied Blocks": 0,
                          "Local Written Blocks": 0,
                          "Temp Read Blocks": 0,
                          "Temp Written Blocks": 0
                        },
                        {
                          "Node Type": "Gather",
                          "Parent Relationship": "Inner",
                          "Parallel Aware": false,
                          "Async Capable": false,
                          "Startup Cost": 1000.00,
                          "Total Cost": 6887.18,
                          "Plan Rows": 40,
                          "Plan Width": 10,
                          "Actual Startup Time": 1.047,
                          "Actual Total Time": 24.818,
                          "Actual Rows": 40,
                          "Actual Loops": 1,
                          "Workers Planned": 1,
                          "Workers Launched": 1,
                          "Single Copy": false,
                          "Shared Hit Blocks": 0,
                          "Shared Read Blocks": 2942,
                          "Shared Dirtied Blocks": 0,
                          "Shared Written Blocks": 0,
                          "Local Hit Blocks": 0,
                          "Local Read Blocks": 0,
                          "Local Dirtied Blocks": 0,
                          "Local Written Blocks": 0,
                          "Temp Read Blocks": 0,
                          "Temp Written Blocks": 0,
                          "Plans": [
                            {
                              "Node Type": "Seq Scan",
                              "Parent Relationship": "Outer",
                              "Parallel Aware": true,
                              "Async Capable": false,
                              "Relation Name": "orders",
                              "Alias": "o",
                              "Startup Cost": 0.00,
                              "Total Cost": 5883.18,
                              "Plan Rows": 24,
                              "Plan Width": 10,
                              "Actual Startup Time": 0.509,
                              "Actual Total Time": 20.885,
                              "Actual Rows": 20,
                              "Actual Loops": 2,
                              "Filter": "(customer_id = 42)",
                              "Rows Removed by Filter": 199980,
                              "Shared Hit Blocks": 0,
                              "Shared Read Blocks": 2942,
                              "Shared Dirtied Blocks": 0,
                              "Shared Written Blocks": 0,
                              "Local Hit Blocks": 0,
                              "Local Read Blocks": 0,
                              "Local Dirtied Blocks": 0,
                              "Local Written Blocks": 0,
                              "Temp Read Blocks": 0,
                              "Temp Written Blocks": 0,
                              "Workers": [
                              ]
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            },
            "Settings": {
            },
            "Planning": {
              "Shared Hit Blocks": 144,
              "Shared Read Blocks": 31,
              "Shared Dirtied Blocks": 1,
              "Shared Written Blocks": 0,
              "Local Hit Blocks": 0,
              "Local Read Blocks": 0,
              "Local Dirtied Blocks": 0,
              "Local Written Blocks": 0,
              "Temp Read Blocks": 0,
              "Temp Written Blocks": 0
            },
            "Planning Time": 1.234,
            "Triggers": [
            ],
            "Execution Time": 25.057
          }
        ]
        """;
}
