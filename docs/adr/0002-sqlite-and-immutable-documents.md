# Use transactional metadata with immutable document files

Use embedded SQLite metadata/indexes and immutable document files rather than a
hand-written JSON transaction database or shared-folder coordination. A single
local writer and durable file-before-metadata publication provide a tractable
failure model without requiring an external database. Native SQLite is isolated
in the file-store package; File-source support for mounted shares does not make
network storage a supported SQLite WAL deployment.
