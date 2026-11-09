using HrPoc.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace HrPoc.Web.Data;

public class HrDbContext : DbContext
{
    public HrDbContext(DbContextOptions<HrDbContext> options) : base(options)
    {
    }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<InstallationConfig> InstallationConfigs => Set<InstallationConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<InstallationConfig>(entity =>
        {
            entity.ToTable("InstallationConfig");
            entity.HasKey(c => c.Id);
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("Employees");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.HasIndex(e => e.PersonnelNumber).IsUnique();
            entity.HasIndex(e => e.LastName);
            entity.HasIndex(e => e.EmploymentStatus);
            entity.HasIndex(e => e.DepartmentId);
            entity.HasIndex(e => e.CostCenterId);
            entity.HasIndex(e => e.LocationId);
            entity.HasIndex(e => e.UpdatedAt);

            entity.HasOne(e => e.Department)
                .WithMany(d => d.Employees)
                .HasForeignKey(e => e.DepartmentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.CostCenter)
                .WithMany(c => c.Employees)
                .HasForeignKey(e => e.CostCenterId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Location)
                .WithMany(l => l.Employees)
                .HasForeignKey(e => e.LocationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Department>(entity =>
        {
            entity.ToTable("Departments");
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => d.StructuredName).IsUnique();
            entity.HasIndex(d => d.ShortName);
            entity.HasIndex(d => d.ParentDepartmentId);

            entity.HasOne(d => d.ParentDepartment)
                .WithMany()
                .HasForeignKey(d => d.ParentDepartmentId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<CostCenter>(entity =>
        {
            entity.ToTable("CostCenters");
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => c.StructuredName);
            entity.HasIndex(c => c.Code);
            entity.HasIndex(c => c.ParentCostCenterId);

            entity.HasOne(c => c.ParentCostCenter)
                .WithMany()
                .HasForeignKey(c => c.ParentCostCenterId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Location>(entity =>
        {
            entity.ToTable("Locations");
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => l.StructuredName).IsUnique();
            entity.HasIndex(l => l.CountryShortcut);
            entity.HasIndex(l => l.City);
        });
    }
}

