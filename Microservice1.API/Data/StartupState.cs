namespace Microservice1.API.Data;

// Uygulamanın açılış işlerini (seed data) bitirip bitirmediğini tutar.
// /health/startup endpoint'i bu bayrağa bakar; Kubernetes startupProbe'u da bu endpoint'i kullanır.
public class StartupState
{
    private volatile bool _isCompleted;

    public bool IsCompleted => _isCompleted;

    public void MarkCompleted() => _isCompleted = true;
}
