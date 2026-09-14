# Keep federation resolution separate from write-through

The scoped federation drafts define read-only resolution, while the product
also needs authorized upstream mutations. Expose the producer-resolved aggregate
as read-only and use separately configured core-HTTP write-through mounts for
single-upstream mutations; do not redefine federation as replication or a
cross-registry transaction. This preserves interoperability and avoids claiming
atomicity that independent upstreams do not provide.
