namespace OrderPricing;

public sealed class OrderService(PricingCalculator pricing, InMemoryOrderRepository repository)
{
    public Receipt Place(OrderRequest order, decimal salesTaxRate)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (string.IsNullOrWhiteSpace(order.OrderId))
            throw new ArgumentException("Order ID is required.", nameof(order));

        // A repeated order ID returns the original receipt without repricing.
        Receipt? existing = repository.Find(order.OrderId);
        if (existing is not null)
            return existing;

        PriceQuote price = pricing.Calculate(order, salesTaxRate);
        var receipt = new Receipt(order.OrderId, price);
        repository.Save(receipt);
        return receipt;
    }
}
