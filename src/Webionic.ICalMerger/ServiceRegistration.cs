using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Feed;
using Webionic.ICalMerger.Fetching;

namespace Webionic.ICalMerger;

public static class ServiceRegistration
{
    public static IServiceCollection AddCalendarServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ICalendarFetcher>(_ =>
        {
            var client = new HttpClient(SafeHttpFetcher.CreateHandler(), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan, // Das Zeitlimit steuert der Fetcher selbst.
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("iCalMerger/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/calendar");
            return new SafeHttpFetcher(client);
        });

        services.Configure<AppLimits>(configuration.GetSection("Limits"));
        services.AddScoped<CalendarService>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISourceCache, SourceCache>();
        services.AddScoped<FeedService>();

        services.AddScoped<Users.CurrentUser>();

        return services;
    }
}
