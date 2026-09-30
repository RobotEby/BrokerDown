using MassTransit;
using Shared.Infrastructure;

namespace Payments.Api;

public sealed class OrderCreatedConsumerDefinition : ConsumerDefinition<OrderCreatedConsumer>
{
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<OrderCreatedConsumer> consumer, IRegistrationContext context)
    {
        endpoint.UseMessageRetry(r =>
        {
            r.Handle<Exception>(Database.IsTransient);
            r.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        });
        endpoint.UseEntityFrameworkOutbox<PaymentsDb>(context);
    }
}
