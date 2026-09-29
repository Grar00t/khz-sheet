# SQLite ledger
`KHZ_ENABLE_LEDGER=ON` links system SQLite; missing development files cause configuration failure. OFF exposes explicit unsupported behavior, not silent in-memory persistence.
The existing ledger stores proof metadata rather than cell payload backups, uses prepared statements/transactions and performs history verification. Existing integrity tests exercise predecessor/hash tampering. Native Linux ledger-on tests were executed; Windows ledger-on was blocked by missing SQLite development files.
No new schema migration system, disk-full simulation, interrupted-write campaign, SQLite fault-injection harness or qualified compliance determination was implemented. WAL durability and filesystem behavior outside the exercised environment remain unverified.
