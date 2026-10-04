using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Webionic.ICalMerger.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<MergedCalendar> Calendars => Set<MergedCalendar>();
    public DbSet<CalendarSource> Sources => Set<CalendarSource>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<MergedCalendar>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(100).IsRequired();
            e.Property(c => c.Token).HasMaxLength(64).IsRequired();
            e.HasIndex(c => c.Token).IsUnique();
            e.HasOne(c => c.Owner).WithMany().HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Sources).WithOne(s => s.MergedCalendar).HasForeignKey(s => s.MergedCalendarId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CalendarSource>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(100).IsRequired();
            e.Property(s => s.Url).HasMaxLength(2000).IsRequired();
            e.Property(s => s.LastError).HasMaxLength(500);
        });
    }
}
