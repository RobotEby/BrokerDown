namespace ChaosLab.IntegrationTests.Infrastructure;

public static class Readiness
{
    public static Task WaitUntilReadyAsync(this HttpClient client)
    {
        var last = "No response";
        return Eventually.Until(async () =>
        {
            using var response = await client.GetAsync("/health/ready");
            last = $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
            return response.IsSuccessStatusCode;
        }, TimeSpan.FromSeconds(60), description: "SQL and MassTransit ready", diagnostic: () => last);
    }
}
