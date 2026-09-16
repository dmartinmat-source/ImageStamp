# Notes

- **What I added.** 
Added a `blur` layer type that applies a Gaussian blur to a bounded canvas region at its configured `zIndex`. The layer contract, input validation, image composition pipeline, PostgreSQL persistence, initialization schema, and tests now support the required `width`, `height`, and positive `sigma` value. A focused renderer test confirms that the blur is constrained to its rectangle and that higher-z-index layers remain sharp.

- **What I ran into and how I dealt with it.** 
The existing PostgreSQL Docker volume had already been initialized, so its schema was not automatically updated by `init.sql`. I added the idempotent `docker/postgres/migrations/001-add-blur-sigma.sql` migration and applied it to the running database. The active Debug API process also locked Debug output files, so validation was run in the Release configuration instead. An existing test used `blur` as its deliberately unsupported type; it was updated to use `unknown` now that blur is valid.

- **What I spotted and chose not to touch.** 
`ImageCompositionService` is registered as a singleton while retaining request-specific mutable fields, including its render queue and copy buffer. That could cause concurrency problems under simultaneous requests, but it is separate from the blur feature and was left unchanged. The repository also performs composition and layer persistence as separate database operations rather than a single transaction.
