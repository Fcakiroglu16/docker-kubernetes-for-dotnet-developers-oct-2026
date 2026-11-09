using System.Diagnostics;

namespace Microservice1.API;

// HPA testi için yapay yük üretir. CPU ve memory yükü birbirinden bağımsızdır.
public sealed class LoadGenerator(ILogger<LoadGenerator> logger)
{
    // Pod OOMKilled olmasın diye tutulan toplam bellek sınırlanır (memory limit 512Mi).
    public const int MaxHeldMb = 250;

    private const int OneMb = 1024 * 1024;

    // Tutulan MemoryStream'ler. Listede durdukları sürece GC onları silemez, bellek dolu kalır.
    private readonly List<MemoryStream> _streams = [];

    public int HeldMb
    {
        get { lock (_streams) return _streams.Sum(s => (int)(s.Length / OneMb)); }
    }

    // CPU yükü: belirtilen süre boyunca CPU'yu meşgul eder (busy loop). İstek bitince CPU kullanımı hemen düşer.
    public void BurnCpu(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        double result = 0;
        while (sw.ElapsedMilliseconds < milliseconds)
            result += Math.Sqrt(result + 1);
    }

    // Memory yükü: mb kadar veriyi bir MemoryStream'e yazar, seconds sn tutar, sonra bırakır.
    // Sınır aşılacaksa hiçbir şey ayırmaz ve false döner.
    public bool AllocateMemory(int mb, int seconds)
    {
        if (HeldMb + mb > MaxHeldMb) return false;

        var stream = new MemoryStream(capacity: mb * OneMb);
        var oneMbOfData = new byte[OneMb];
        Array.Fill(oneMbOfData, (byte)1);
        // Veri yazılmadan ayrılan bellek fiziksel olarak kullanılmaz ve HPA'nın baktığı working set'e yansımaz.
        for (var i = 0; i < mb; i++)
            stream.Write(oneMbOfData);

        lock (_streams) _streams.Add(stream);

        // Süre dolunca stream'i listeden çıkar ve belleği geri ver.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            lock (_streams) _streams.Remove(stream);
            stream.Dispose();
            ReturnMemoryToOs();
        });

        return true;
    }

    // Tutulan tüm belleği hemen bırakır.
    public void ReleaseAll()
    {
        lock (_streams)
        {
            _streams.ForEach(s => s.Dispose());
            _streams.Clear();
        }
        ReturnMemoryToOs();
    }

    // Normal GC boşalan belleği işletim sistemine hemen geri vermeyebilir; working set yüksek kalır ve HPA küçülmez.
    // Aggressive mod belleği sıkıştırır ve kullanılmayan kısmı işletim sistemine iade eder.
    private void ReturnMemoryToOs()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        logger.LogInformation("Bellek bırakıldı. Tutulan: {HeldMb} MB, working set: {WorkingSetMb} MB",
            HeldMb, Environment.WorkingSet / OneMb);
    }
}
