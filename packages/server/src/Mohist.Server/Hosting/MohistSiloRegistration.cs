namespace Mohist.Server.Hosting;

public static class MohistSiloRegistration
{
    public static ISiloBuilder ConfigureMohistSilo(this ISiloBuilder silo)
    {
        silo.UseLocalhostClustering();

        silo.ConfigureLogging(logging =>
        {
            logging.AddConsole();
        });

        return silo;
    }
}
