using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Groundwork.Tests")]
// Groundwork.Sqlite's relationship-transition executor replays Core's internal durable failure
// envelope (RelationshipMaterialization* Restore members); this grant is load-bearing.
[assembly: InternalsVisibleTo("Groundwork.Sqlite")]
