## v.0.1.7.3

Fixed multiple bugs in lane connection persistence logic, old data will be automatically migrated. If unrecoverable configuration errors are detected, lane connections at the intersection **will be reset** and added to the migration summary for easier manual lane connection recreation, if needed.

- improved lane connection settings persistence when applying road upgrades,
- improved the mod stability when modifying intersection connections causing the game crash in rare cases, 
- extended lane configuration data with more information,
- automatic data migration tool with UI for displaying migration results,
- increased the click detection radius on **Lane Connectors**, increasing connector size in the options will also increase detection radius,
- fixed minor custom lane connection preview if they are going to be deleted - should now display vanilla, instead of nothing 
