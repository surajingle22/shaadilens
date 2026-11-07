namespace ShaadiLens.Domain.Entities;

/// <summary>A function within the wedding: Haldi, Mehendi, Sangeet, Wedding, Reception…</summary>
public class Collection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsHidden { get; set; }

    public Event? Event { get; set; }
}
