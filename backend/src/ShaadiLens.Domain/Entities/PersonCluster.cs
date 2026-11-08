namespace ShaadiLens.Domain.Entities;

/// <summary>
/// "Person 7": a group of faces that the clustering job (DBSCAN) thinks belong to one person.
/// The admin can give it a name and link it to a guest — then those photos show up for that guest.
/// </summary>
public class PersonCluster
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    /// <summary>1 = the person with the most faces (bride/groom, usually).</summary>
    public int Number { get; set; }
    public string? Name { get; set; }
    /// <summary>Linked guest: the cluster's faces were added to their references and photos.</summary>
    public Guid? GuestId { get; set; }
    public int FaceCount { get; set; }
    public int PhotoCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
