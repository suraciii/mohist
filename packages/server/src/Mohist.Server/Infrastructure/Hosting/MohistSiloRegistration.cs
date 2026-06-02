namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistSiloRegistration
{
    public static ISiloBuilder ConfigureMohistSilo(this ISiloBuilder silo, IConfiguration configuration)
    {
        silo.UseLocalhostClustering();
        silo.UseAdoNetReminderService(options =>
        {
            options.Invariant = "System.Data.SQLite";
            options.ConnectionString = MohistServiceRegistration.ResolveSqliteConnectionString(configuration);
        });

        silo.ConfigureLogging(logging =>
        {
            logging.AddConsole();
        });

        return silo;
    }
}
