using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Jaq;

public class Staff
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "attendant";
    public bool Active { get; set; } = true;
    public bool CanProvide { get; set; } = true;
    public string? Username { get; set; }
    public string? PasswordHash { get; set; }
    public int SecurityVersion { get; set; }
}
public class Service
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public long Price { get; set; }
    public long Payout { get; set; }
    public bool Reviewed { get; set; }
    public string Notes { get; set; } = "";
}
public class Client
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Preferences { get; set; } = "";
    public string ChemicalHistory { get; set; } = "";
    public string BlondGoal { get; set; } = "";
    public string Formula { get; set; } = "";
    public string StrandTest { get; set; } = "";
    public string MegaTechnique { get; set; } = "";
    public string HairOrigin { get; set; } = "";
    public string HairColor { get; set; } = "";
    public string HairLength { get; set; } = "";
    public string HairQuantity { get; set; } = "";
    public DateOnly? InstallationDate { get; set; }
    public DateOnly? NextReturn { get; set; }
    public string ReturnContact { get; set; } = "Pendente";
}
public class Visit
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;
    public string Status { get; set; } = "scheduled";
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<VisitItem> Items { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    [ConcurrencyCheck] public int Version { get; set; }
}
public class VisitItem
{
    public int Id { get; set; }
    public int VisitId { get; set; }
    public int ServiceId { get; set; }
    public int StaffId { get; set; }
    public string ServiceName { get; set; } = "";
    public string StaffName { get; set; } = "";
    public long Price { get; set; }
    public long Payout { get; set; }
    public DateTime StartsAt { get; set; }
    public int Minutes { get; set; }
    public bool Removed { get; set; }
    public DateTime? PayoutPaidAt { get; set; }
}
public class Payment
{
    public int Id { get; set; }
    public int VisitId { get; set; }
    public long Amount { get; set; }
    public string Method { get; set; } = "";
    public string Kind { get; set; } = "payment";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Audit
{
    public int Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public int ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}
public class SalonDb(DbContextOptions<SalonDb> options) : DbContext(options)
{
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<VisitItem> Items => Set<VisitItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Audit> Audits => Set<Audit>();
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Staff>().HasIndex(x => x.Username).IsUnique();
        m.Entity<Visit>().HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<VisitItem>().HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<VisitItem>().HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<VisitItem>().HasIndex(x => new { x.StaffId, x.StartsAt });
    }
}
public record LoginInput([property: JsonRequired] string Username, [property: JsonRequired] string Password);
public record SetupInput([property: JsonRequired] string Name, [property: JsonRequired] string Username, [property: JsonRequired] string Password);
public record StaffInput([property: JsonRequired] string Name, [property: JsonRequired] string Role, [property: JsonRequired] bool Active, [property: JsonRequired] bool CanProvide, string? Username, string? Password);
public record ServiceInput([property: JsonRequired] string Name, [property: JsonRequired] long Price, [property: JsonRequired] long Payout, [property: JsonRequired] bool Reviewed, string Notes);
public record VisitInput([property: JsonRequired] int ClientId, string Notes);
public record ItemInput([property: JsonRequired] int ServiceId, [property: JsonRequired] int StaffId, [property: JsonRequired] DateTime StartsAt, [property: JsonRequired] int Minutes, long? Payout);
public record AssignmentInput([property: JsonRequired] int StaffId, [property: JsonRequired] DateTime StartsAt, [property: JsonRequired] int Minutes, long? Payout);
public record PaymentInput([property: JsonRequired] long Amount, [property: JsonRequired] string Method, [property: JsonRequired] string Kind);
public record StatusInput([property: JsonRequired] string Status);
public class RuleException(string message, int code = 400) : Exception(message) { public int Code => code; }
