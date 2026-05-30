# 📘 MCP Tools Reference

## Overview

hs-sql-agent exposes MCP tools that AI agents can discover and invoke. Tools are dynamically filtered per session based on the API key's allowed tools configuration.

## Data Query (DQL) Tools

### `execute_query_safe`

Execute a complex SQL query with structured parameters.

- **Read-only**: ✅ True
- **Parameters**:

| Parameter         | Type   | Description                                    |
| ----------------- | ------ | ---------------------------------------------- |
| `tableName`       | string | Main table to query                            |
| `selectColumns`   | array  | Columns to select (field, function, arithmetic) |
| `whereConditions` | array  | Filter conditions                              |
| `joins`           | array  | JOIN clauses                                   |
| `groupBy`         | array  | GROUP BY columns                               |
| `having`          | array  | HAVING conditions                              |
| `orderBy`         | array  | ORDER BY columns/directions                    |
| `limit`           | number | Maximum rows to return                         |
| `offset`          | number | Row offset for pagination                      |
| `combineType`     | string | UNION / UNION ALL / INTERSECT / EXCEPT         |
| `combineQueries`  | array  | Combined sub-queries                           |

### `get_columns`

Get column names and data types for a specific table.

- **Read-only**: ✅ True
- **Parameters**:

| Parameter   | Type   | Description                     |
| ----------- | ------ | ------------------------------- |
| `tableName` | string | Name of the table               |
| `schema`    | string | Schema name (optional)          |

- **Response**: Returns column metadata including:
  - Column name
  - Data type
  - Is nullable
  - Semantic descriptions (if defined in Semantic Layer)

### `get_tables`

Get all tables in the database (respects table whitelist if configured).

- **Read-only**: ✅ True
- **Parameters**:

| Parameter | Type   | Description                     |
| --------- | ------ | ------------------------------- |
| `schema`  | string | Schema name (optional)          |

- **Response**: Returns table list including:
  - Table name
  - Schema
  - Semantic descriptions (if defined in Semantic Layer)

### `get_schemas`

Get schemas in the database.

- **Read-only**: ✅ True
- **Parameters**: None

## Data Manipulation (DML) Tools

### `execute_dml_safe`

Execute INSERT, UPDATE, or DELETE with a two-step confirmation process.

- **Read-only**: ❌ False
- **Safety Protocol**:

1. **Dry-run call**: The AI first calls with `confirm: false` to preview the SQL
2. **Review**: The user reviews the generated SQL
3. **Confirm**: The AI calls again with the returned `ConfirmToken` and `confirm: true`
4. **Execute**: The DML is executed only if the token is valid

- **Parameters**:

| Parameter       | Type    | Description                                      |
| --------------- | ------- | ------------------------------------------------ |
| `tableName`     | string  | Target table                                     |
| `operation`     | string  | INSERT / UPDATE / DELETE                         |
| `values`        | object  | Column-value pairs (for INSERT/UPDATE)           |
| `whereConditions`| array  | Filter conditions (for UPDATE/DELETE)            |
| `confirmToken`  | string  | Token from dry-run to confirm execution          |
| `confirm`       | boolean | Set to `true` to commit after dry-run            |

## Custom Tools Strategy

> **Use Custom Tools for any query that is predictable, repetitive, or domain-specific.** The more complex the query, the more you benefit from Custom Tools over generic `execute_query_safe`.

### Why Custom Tools?

| Concern | `execute_query_safe` (Common Query) | Custom Tool |
|---|---|---|
| **LLM thinking tokens** | High — LLM must decide tables, columns, joins, conditions from scratch each time | Minimal — LLM passes only a few variable parameters (`{{param}}`) |
| **Parameter passing** | Unstructured — LLM sends large nested JSON objects | Structured — LLM sends only the handful of `{{param}}` values |
| **Hallucination risk** | Higher — LLM can invent column names, join types, or filter logic | Lower — SQL is pre-defined by admin, LLM only fills values |
| **Query complexity** | Limited by what the LLM can reliably express | Unlimited — supports any SQL the admin can write |
| **Token usage per call** | Large (full query definition) | Small (only parameter values) |
| **Determinism** | Depends on LLM reasoning | Fully deterministic — SQL is fixed, only params vary |

### When to use each approach

| Scenario | Recommended Tool |
|---|---|
| **Exploratory/ad-hoc queries** — "Show me the users who signed up last month" | `execute_query_safe` (flexible) |
| **One-off schema discovery** — "What tables exist?" | `get_tables` / `get_columns` / `get_schemas` |
| **Repetitive business queries** — "Calculate churn rate for Q1 2026" | **Custom Tool** (e.g., `calculate_churn_rate`) |
| **Complex joins with fixed structure** — "Get order details with customer info for order X" | **Custom Tool** (SQL is fixed, just pass order ID) |
| **Domain metrics** — "What's the current inventory value for warehouse Y?" | **Custom Tool** |
| **Uncertain/unpredictable queries** — "I need to explore the data, not sure what I'm looking for" | `execute_query_safe` |

### How to create an effective Custom Tool

1. **Identify the query pattern** — What SQL do you find yourself running repeatedly?
2. **Generalize with parameters** — Use `{{parameterName}}` for the parts that change
3. **Test in Admin Panel** — Verify the tool works with sample inputs
4. **Name it descriptively** — The AI agent will see the tool name and description, so make them clear (e.g., `calculate_churn_rate`)
5. **Document parameters well** — Good descriptions help the LLM understand what to pass

### Example

A custom tool `get_customer_orders` with:
- **SQL**: `SELECT * FROM orders WHERE customer_id = {{customerId}} AND status = {{status}} ORDER BY created_at DESC LIMIT {{limit}}`
- **Parameters**: `customerId` (number), `status` (string), `limit` (number)
- **AI invocation**: LLM passes `{ customerId: 42, status: "shipped", limit: 10 }` — no SQL knowledge needed

### Summary

- ✅ **Use Custom Tools** for any query you can predict in advance
- ✅ **Use `execute_query_safe`** for exploration and unpredictable analysis
- ✅ Custom Tools = fewer tokens, less hallucination, more precise results

## Custom Tool Technical Details

Custom tools are defined in the Admin Panel and automatically exposed as MCP tools. They support:

- **Parameterized queries** with `{{parameterName}}` syntax
- **Query (SELECT)** or **DML (INSERT/UPDATE/DELETE)** operations
- **Dynamic parameter injection** — AI passes context-aware arguments

Each custom tool appears as a separate MCP function with its own schema based on the defined parameters.

## Tool Execution Flow

```
AI Agent → Tool Discovery (list tools)
  → Select Tool → Invoke with parameters
    → [Auth] Validate API key permissions
    → [Whitelist] Check table access
    → [Param] Parse and parameterize inputs via SqlKata
    → [Execute] Run against target database
    → [Audit] Log execution details
    → Return result to AI Agent
```

## Future Tools

| Tool                    | Status | Description                                   |
| ----------------------- | ------ | --------------------------------------------- |
| `save_query`            | 🔜     | Save query for AI agent                       |
| `update_semantic_layer` | 🔜     | Let AI agent update/enrich the Semantic Layer  |
