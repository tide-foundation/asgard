// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// A compile time assertion, not a runtime test. The docs claim a node-postgres
// Pool or Client satisfies SqlClient as is, which is what lets the package take
// a connection without depending on a driver. If that ever stops being true this
// file stops compiling.

import { Client, Pool, PoolClient } from "pg";
import { SqlClient } from "../../src/scheduler/execution/PostgresJobStore";

declare const pool: Pool;
declare const client: Client;
declare const pooled: PoolClient;

export const poolIsSqlClient: SqlClient = pool;
export const clientIsSqlClient: SqlClient = client;
export const pooledIsSqlClient: SqlClient = pooled;
