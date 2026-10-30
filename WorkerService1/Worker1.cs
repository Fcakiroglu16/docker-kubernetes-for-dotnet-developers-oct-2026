namespace WorkerService1;

public class Worker1(ILogger<Worker1> logger) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        
        
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
     
        
        Console.WriteLine("Worker2 running at: {0}", DateTimeOffset.Now);
    }
}