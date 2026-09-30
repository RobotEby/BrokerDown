using MassTransit;
using Shared.Infrastructure;

namespace Orders.Api;

public sealed class PaymentProcessedConsumerDefinition : ConsumerDefinition<PaymentProcessedConsumer>
{
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<PaymentProcessedConsumer> consumer, IRegistrationContext context)
    {
        endpoint.UseMessageRetry(r =>
        {
            r.Handle<Exception>(Database.IsTransient);
            r.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        });
        endpoint.UseEntityFrameworkOutbox<OrdersDb>(context);
    }
}
